using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Impl.AdoJobStore;
using Quartz.Spi;
using Quartz.Util;
using Shoko.Server.Scheduling.Acquisition.Filters;
using Shoko.Server.Scheduling.Concurrency;
using Shoko.Server.Scheduling.Delegates;
using Shoko.Server.Utilities;

namespace Shoko.Server.Scheduling;

public class ThreadPooledJobStore : JobStoreTX
{
    private readonly ILogger<ThreadPooledJobStore> _logger;
    private readonly QueueStateEventHandler _queueStateEventHandler;
    private readonly JobFactory _jobFactory;
    private ITypeLoadHelper _typeLoadHelper;
    private readonly Dictionary<JobKey, (IJobDetail Job, DateTime StartTime)> _executingJobs = new();
    private readonly IAcquisitionFilter[] _acquisitionFilters;
    private Dictionary<Type, int> _typeConcurrencyCache;
    private Dictionary<string, Type[]> _concurrencyGroupCache;
    private int _threadPoolSize;

    public ThreadPooledJobStore(ILogger<ThreadPooledJobStore> logger, IEnumerable<IAcquisitionFilter> acquisitionFilters,
        QueueStateEventHandler queueStateEventHandler, JobFactory jobFactory)
    {
        _logger = logger;
        _queueStateEventHandler = queueStateEventHandler;
        _jobFactory = jobFactory;
        _acquisitionFilters = acquisitionFilters.ToArray();
        foreach (var filter in _acquisitionFilters) filter.StateChanged += FilterOnStateChanged;
        InitConcurrencyCache();
    }

    public override async Task Initialize(ITypeLoadHelper loadHelper, ISchedulerSignaler signaler, CancellationToken cancellationToken = default)
    {
        _typeLoadHelper = loadHelper;
        await base.Initialize(loadHelper, signaler, cancellationToken);
    }

    private void InitConcurrencyCache()
    {
        _concurrencyGroupCache = new Dictionary<string, Type[]>();
        _typeConcurrencyCache = new Dictionary<Type, int>();
        var types = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes())
            .Where(a => typeof(IJob).IsAssignableFrom(a) && !a.IsAbstract).ToList();

        foreach (var type in types)
        {
            var attribute = type.GetCustomAttribute<LimitConcurrencyAttribute>();
            if (attribute != null)
            {
                _typeConcurrencyCache[type] = attribute.MaxConcurrentJobs;
            }

            var concurrencyGroup = type.GetCustomAttribute<DisallowConcurrencyGroupAttribute>();
            if (concurrencyGroup != null)
            {
                if (_concurrencyGroupCache.TryGetValue(concurrencyGroup.Group, out var groupTypes)) groupTypes = groupTypes.Append(type).Distinct().ToArray();
                else groupTypes = new[] { type };
                _concurrencyGroupCache[concurrencyGroup.Group] = groupTypes;
            }
        }

        var overrides = Utils.SettingsProvider.GetSettings().Quartz.LimitedConcurrencyOverrides;
        if (overrides == null) return;
        foreach (var kv in overrides)
        {
            var type = Assembly.GetExecutingAssembly().GetTypes().FirstOrDefault(a => a.Name.Equals(kv.Key));
            if (type == null) continue;
            var value = kv.Value;
            var attribute = type.GetCustomAttribute<LimitConcurrencyAttribute>();
            if (attribute is { MaxAllowedConcurrentJobs: > 0 } && attribute.MaxAllowedConcurrentJobs < kv.Value) value = attribute.MaxAllowedConcurrentJobs;
            _typeConcurrencyCache[type] = value;
        }
    }

    ~ThreadPooledJobStore()
    {
        foreach (var filter in _acquisitionFilters) filter.StateChanged -= FilterOnStateChanged;
    }

    private void FilterOnStateChanged(object sender, EventArgs e)
    {
        SignalSchedulingChangeImmediately(new DateTimeOffset(1982, 6, 28, 0, 0, 0, TimeSpan.FromSeconds(0)));
    }

    protected override async Task StoreTrigger(ConnectionAndTransactionHolder conn, IOperableTrigger newTrigger, IJobDetail job, bool replaceExisting, string state, bool forceState,
        bool recovering, CancellationToken cancellationToken = new CancellationToken())
    {
        await base.StoreTrigger(conn, newTrigger, job, replaceExisting, state, forceState, recovering, cancellationToken);
        await JobStoringQueueEvents(conn, job, cancellationToken);
    }

    protected override async Task<IReadOnlyCollection<IOperableTrigger>> AcquireNextTrigger(ConnectionAndTransactionHolder conn, DateTimeOffset noLaterThan,
            int maxCount, TimeSpan timeWindow, CancellationToken cancellationToken = default)
    {
        if (timeWindow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeWindow));

        const int MaxDoLoopRetry = 3;
        var acquiredTriggers = new List<IOperableTrigger>();
        var acquiredJobsWithLimitedConcurrency = new Dictionary<string, int>();
        var currentLoopCount = 0;

        do
        {
            currentLoopCount++;
            try
            {
                var filteringTypes = GetTypes();
                var results = await Delegate.SelectTriggerToAcquire(conn, noLaterThan + timeWindow, MisfireTime, maxCount, filteringTypes, cancellationToken)
                    .ConfigureAwait(false);

                // No trigger is ready to fire yet.
                if (results.Count == 0) return acquiredTriggers;
                var batchEnd = noLaterThan;

                foreach (var result in results)
                {
                    var triggerKey = new TriggerKey(result.TriggerName, result.TriggerGroup);

                    // If our trigger is no longer available, try a new one.
                    var nextTrigger = await RetrieveTrigger(conn, triggerKey, cancellationToken).ConfigureAwait(false);
                    if (nextTrigger == null) continue; // next trigger

                    // If trigger's job is set as @DisallowConcurrentExecution, and it has already been added to result, then
                    // put it back into the timeTriggers set and continue to search for next trigger.
                    Type jobType;
                    try
                    {
                        jobType = _typeLoadHelper.LoadType(result.JobType)!;
                    }
                    catch (JobPersistenceException jpe)
                    {
                        try
                        {
                            _logger.LogError(jpe, "Error retrieving job, setting trigger state to ERROR");
                            await Delegate.UpdateTriggerState(conn, triggerKey, StateError, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Unable to set trigger state to ERROR");
                        }
                        continue;
                    }

                    if (!JobAllowed(jobType, acquiredJobsWithLimitedConcurrency)) continue;

                    var nextFireTimeUtc = nextTrigger.GetNextFireTimeUtc();

                    // A trigger should not return NULL on nextFireTime when fetched from DB.
                    // But for whatever reason if we do have this (BAD trigger implementation or
                    // data?), we then should log a warning and continue to next trigger.
                    // User would need to manually fix these triggers from DB as they will not
                    // able to be clean up by Quartz since we are not returning it to be processed.
                    if (nextFireTimeUtc == null)
                    {
                        _logger.LogWarning("Trigger {NextTriggerKey} returned null on nextFireTime and yet still exists in DB!", nextTrigger.Key);
                        continue;
                    }

                    if (nextFireTimeUtc > batchEnd) break;

                    // We now have a acquired trigger, let's add to return list.
                    // If our trigger was no longer in the expected state, try a new one.
                    var rowsUpdated = await Delegate.UpdateTriggerStateFromOtherStateWithNextFireTime(conn, triggerKey, StateAcquired, StateWaiting, nextFireTimeUtc.Value, cancellationToken).ConfigureAwait(false);
                    if (rowsUpdated <= 0) continue; // next trigger

                    nextTrigger.FireInstanceId = GetFiredTriggerRecordId();
                    await Delegate.InsertFiredTrigger(conn, nextTrigger, StateAcquired, null, cancellationToken).ConfigureAwait(false);

                    if (acquiredTriggers.Count == 0)
                    {
                        var now = SystemTime.UtcNow();
                        var nextFireTime = nextFireTimeUtc.Value;
                        var max = now > nextFireTime ? now : nextFireTime;

                        batchEnd = max + timeWindow;
                    }

                    acquiredTriggers.Add(nextTrigger);
                }

                // if we didn't end up with any trigger to fire from that first
                // batch, try again for another batch. We allow with a max retry count.
                if (acquiredTriggers.Count == 0 && currentLoopCount < MaxDoLoopRetry) continue;

                // We are done with the while loop.
                break;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error in Acquiring Next Trigger");
                throw new JobPersistenceException("Couldn't acquire next trigger: " + e.Message, e);
            }
        } while (true);

        // Return the acquired trigger list
        return acquiredTriggers;
    }

    private (IEnumerable<Type> TypesToExclude, Dictionary<Type, int> TypesToLimit) GetTypes()
    {
        var excludedTypes = new List<Type>();
        var limitedTypes = new Dictionary<Type, int>();
        foreach (var filter in _acquisitionFilters) excludedTypes.AddRange(filter.GetTypesToExclude());

        IEnumerable<(Type Type, int Count)> executingTypes;
        lock (_executingJobs)
            executingTypes = _executingJobs.Values.Select(a => a.Job.JobType).GroupBy(a => a).Select(a => (Type: a.Key, Count: a.Count())).ToList();

        foreach (var kv in _typeConcurrencyCache)
        {
            var executing = executingTypes.FirstOrDefault(a => a.Type == kv.Key);
            // kv.Value is the max count, we want to get the number of remaining jobs we can run
            var limit = executing == default ? kv.Value : kv.Value - executing.Count;
            if (limit <= 0) excludedTypes.Add(kv.Key);
            else if (!excludedTypes.Contains(kv.Key)) limitedTypes[kv.Key] = limit;
        }

        foreach (var kv in _concurrencyGroupCache)
        {
            var executing = kv.Value.Any(a => executingTypes.Any(b => b.Type == a));
            if (executing)
            {
                excludedTypes.AddRange(kv.Value);
                continue;
            }

            foreach (var limitedType in kv.Value)
            {
                if (excludedTypes.Contains(limitedType)) continue;
                // we only allow one concurrent job in a concurrency group, for example only 1 AniDB command
                limitedTypes[limitedType] = 1;
            }
        }

        return (excludedTypes.Distinct().ToList(), limitedTypes);
    }

    private bool JobAllowed(Type jobType, Dictionary<string, int> acquiredJobTypesWithLimitedConcurrency)
    {
        if (ObjectUtils.IsAttributePresent(jobType, typeof(DisallowConcurrentExecutionAttribute)))
        {
            lock (_executingJobs)
                if (_executingJobs.Values.Any(a => a.Job.JobType == jobType))
                    return false;
            if (acquiredJobTypesWithLimitedConcurrency.TryGetValue(jobType.Name, out var number) && number >= 1) return false;
            acquiredJobTypesWithLimitedConcurrency[jobType.Name] = number + 1;
            return true;
        }

        if (jobType.GetCustomAttributes().FirstOrDefault(a => a is DisallowConcurrencyGroupAttribute) is DisallowConcurrencyGroupAttribute concurrencyAttribute)
        {
            lock (_executingJobs)
                if(_executingJobs.Values.Any(a => _concurrencyGroupCache[concurrencyAttribute.Group].Contains(a.Job.JobType))) return false;
            if (acquiredJobTypesWithLimitedConcurrency.TryGetValue(concurrencyAttribute.Group, out var number)) return false;
            acquiredJobTypesWithLimitedConcurrency[concurrencyAttribute.Group] = number + 1;
            return true;
        }

        if (_typeConcurrencyCache.TryGetValue(jobType, out var maxJobs) && maxJobs > 0)
        {
            int count;
            lock (_executingJobs)
                count = _executingJobs.Values.Count(a => a.Job.JobType == jobType);
            acquiredJobTypesWithLimitedConcurrency.TryGetValue(jobType.Name, out var number);
            acquiredJobTypesWithLimitedConcurrency[jobType.Name] = number + 1;
            return number + count < maxJobs;
        }

        if (jobType.GetCustomAttributes().FirstOrDefault(a => a is LimitConcurrencyAttribute) is LimitConcurrencyAttribute limitConcurrencyAttribute)
        {
            if (!_typeConcurrencyCache.TryGetValue(jobType, out var maxConcurrentJobs)) maxConcurrentJobs = limitConcurrencyAttribute.MaxConcurrentJobs;
            if (maxConcurrentJobs <= 0) maxConcurrentJobs = 1;
            acquiredJobTypesWithLimitedConcurrency.TryGetValue(jobType.Name, out var number);
            acquiredJobTypesWithLimitedConcurrency[jobType.Name] = number + 1;
            int count;
            lock (_executingJobs)
                count = _executingJobs.Values.Count(a => a.Job.JobType == jobType);
            return number + count < maxConcurrentJobs;
        }

        return true;
    }
    
    public override async Task<IReadOnlyCollection<TriggerFiredResult>> TriggersFired(IReadOnlyCollection<IOperableTrigger> triggers, CancellationToken cancellationToken = default)
    {
        return await ExecuteInNonManagedTXLock(LockTriggerAccess, async conn => await TriggersFiredCallback(conn, triggers, cancellationToken),
            async (conn, result) => await TriggersFiredValidator(conn, result, cancellationToken), cancellationToken);
    }

    private async Task<IReadOnlyCollection<TriggerFiredResult>> TriggersFiredCallback(ConnectionAndTransactionHolder conn, IReadOnlyCollection<IOperableTrigger> triggers, CancellationToken cancellationToken)
    {
        List<TriggerFiredResult> results = new(triggers.Count);

        foreach (var trigger in triggers)
        {
            TriggerFiredResult result;
            try
            {
                var bundle = await TriggerFired(conn, trigger, cancellationToken).ConfigureAwait(false);
                result = new TriggerFiredResult(bundle);
            }
            catch (JobPersistenceException jpe)
            {
                _logger.LogError(jpe, "Caught job persistence exception: {Ex}", jpe.Message);
                result = new TriggerFiredResult(jpe);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Caught exception: {Ex}", ex.Message);
                result = new TriggerFiredResult(ex);
            }

            results.Add(result);
        }

        return results;
    }

    private async Task<bool> TriggersFiredValidator(ConnectionAndTransactionHolder conn, IEnumerable<TriggerFiredResult> result, CancellationToken cancellationToken)
    {
        try
        {
            var acquired = await Delegate.SelectInstancesFiredTriggerRecords(conn, InstanceId, cancellationToken).ConfigureAwait(false);
            var executingTriggers = acquired.Where(ft => StateExecuting.Equals(ft.FireInstanceState)).Select(a => a.FireInstanceId).ToHashSet();
            return result.Any(tr => tr.TriggerFiredBundle != null && executingTriggers.Contains(tr.TriggerFiredBundle.Trigger.FireInstanceId));
        }
        catch (Exception e)
        {
            throw new JobPersistenceException("error validating trigger acquisition", e);
        }
    }

    protected override async Task<TriggerFiredBundle> TriggerFired(ConnectionAndTransactionHolder conn, IOperableTrigger trigger, CancellationToken cancellationToken = default)
    {
        IJobDetail job;
        ICalendar cal = null;

        // Make sure trigger wasn't deleted, paused, or completed...
        try
        {
            // if trigger was deleted, state will be StateDeleted
            var state = await Delegate.SelectTriggerState(conn, trigger.Key, cancellationToken).ConfigureAwait(false);
            if (!state.Equals(StateAcquired)) return null;
        }
        catch (Exception e)
        {
            throw new JobPersistenceException("Couldn't select trigger state: " + e.Message, e);
        }

        try
        {
            job = await RetrieveJob(conn, trigger.JobKey, cancellationToken).ConfigureAwait(false);
            if (job == null) return null;
        }
        catch (JobPersistenceException jpe)
        {
            try
            {
                _logger.LogError(jpe, "Error retrieving job, setting trigger state to ERROR");
                await Delegate.UpdateTriggerState(conn, trigger.Key, StateError, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception sqle)
            {
                _logger.LogError(sqle, "Unable to set trigger state to ERROR");
            }
            throw;
        }

        if (trigger.CalendarName != null)
        {
            cal = await RetrieveCalendar(conn, trigger.CalendarName, cancellationToken).ConfigureAwait(false);
            if (cal == null) return null;
        }

        try
        {
            await Delegate.UpdateFiredTrigger(conn, trigger, StateExecuting, job, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new JobPersistenceException("Couldn't update fired trigger: " + e.Message, e);
        }

        var prevFireTime = trigger.GetPreviousFireTimeUtc();

        // call triggered - to update the trigger's next-fire-time state...
        trigger.Triggered(cal);

        var (state2, force) = await UpdateTriggerStatesForLimitedConcurrency(conn, job, cancellationToken);

        if (!trigger.GetNextFireTimeUtc().HasValue)
        {
            state2 = StateComplete;
            force = true;
        }

        await StoreTrigger(conn, trigger, job, true, state2, force, false, cancellationToken).ConfigureAwait(false);

        job.JobDataMap.ClearDirtyFlag();

        await JobFiringQueueEvents(conn, trigger, job, cancellationToken);
        
        return new TriggerFiredBundle(
            job,
            trigger,
            cal,
            trigger.Key.Group.Equals(SchedulerConstants.DefaultRecoveryGroup),
            SystemTime.UtcNow(),
            trigger.GetPreviousFireTimeUtc(),
            prevFireTime,
            trigger.GetNextFireTimeUtc());
    }

    private async Task<(string state2, bool force)> UpdateTriggerStatesForLimitedConcurrency(ConnectionAndTransactionHolder conn, IJobDetail job, CancellationToken cancellationToken)
    {
        var jobTypesWithLimitedConcurrency = new Dictionary<string, int>();
        lock (_executingJobs)
        {
            foreach (var executingJob in _executingJobs)
            {
                if (Equals(executingJob.Key, job.Key)) continue;
                if (!JobAllowed(job.JobType, jobTypesWithLimitedConcurrency)) goto loopBreak;
            }
        }

        if (JobAllowed(job.JobType, jobTypesWithLimitedConcurrency)) return (StateWaiting, true);

        loopBreak:
        try
        {
            var types = new[] { job.JobType };
            if (job.JobType.GetCustomAttributes().FirstOrDefault(a => a is DisallowConcurrencyGroupAttribute) is DisallowConcurrencyGroupAttribute
                concurrencyAttribute)
            {
                types = _concurrencyGroupCache[concurrencyAttribute.Group];
            }
            await Delegate.UpdateTriggerStatesForJobFromOtherState(conn, types, StateBlocked, StateWaiting, cancellationToken).ConfigureAwait(false);
            await Delegate.UpdateTriggerStatesForJobFromOtherState(conn, types, StateBlocked, StateAcquired, cancellationToken).ConfigureAwait(false);
            await Delegate.UpdateTriggerStatesForJobFromOtherState(conn, types, StatePausedBlocked, StatePaused, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            throw new JobPersistenceException("Couldn't update states of blocked triggers: " + e.Message, e);
        }

        return (StateBlocked, false);
    }

    public Task<int> GetWaitingTriggersCount()
    {
        return ExecuteInNonManagedTXLock(LockTriggerAccess, async conn => await GetWaitingTriggersCount(conn), new CancellationToken());
    }

    private Task<int> GetWaitingTriggersCount(ConnectionAndTransactionHolder conn, CancellationToken cancellationToken = new CancellationToken())
    {
        var types = GetTypes();
        return Delegate.SelectWaitingTriggerCount(conn, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30), MisfireTime, types, cancellationToken);
    }

    public Task<int> GetBlockedTriggersCount()
    {
        return ExecuteInNonManagedTXLock(LockTriggerAccess, async conn => await GetBlockedTriggersCount(conn), new CancellationToken());
    }

    private Task<int> GetBlockedTriggersCount(ConnectionAndTransactionHolder conn, CancellationToken cancellationToken = new CancellationToken())
    {
        var types = GetTypes();
        return Delegate.SelectBlockedTriggerCount(conn, _typeLoadHelper, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30), MisfireTime, types,
            cancellationToken);
    }

    public Task<int> GetTotalWaitingTriggersCount()
    {
        return ExecuteInNonManagedTXLock(LockTriggerAccess, async conn => await GetTotalWaitingTriggersCount(conn), new CancellationToken());
    }

    private Task<int> GetTotalWaitingTriggersCount(ConnectionAndTransactionHolder conn, CancellationToken cancellationToken = new CancellationToken())
    {
        return Delegate.SelectTotalWaitingTriggerCount(conn, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30), MisfireTime, cancellationToken);
    }

    public Task<Dictionary<Type, int>> GetJobCounts()
    {
        return ExecuteInNonManagedTXLock(LockTriggerAccess, async conn => await GetJobCounts(conn), new CancellationToken());
    }

    private Task<Dictionary<Type, int>> GetJobCounts(ConnectionAndTransactionHolder conn, CancellationToken cancellationToken = new CancellationToken())
    {
        return Delegate.SelectJobTypeCounts(conn, _typeLoadHelper, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30), cancellationToken);
    }

    public Task<List<QueueItem>> GetJobs(int maxCount, int offset)
    {
        return ExecuteInNonManagedTXLock(LockTriggerAccess, async conn => await GetJobs(conn, maxCount, offset), new CancellationToken());
    }

    private async Task<List<QueueItem>> GetJobs(ConnectionAndTransactionHolder conn, int maxCount, int offset, CancellationToken cancellationToken = new CancellationToken())
    {
        var types = GetTypes();

        var result = new List<QueueItem>();
        lock (_executingJobs)
        {
            if (offset < _executingJobs.Count)
            {
                result.AddRange(_executingJobs.Values.Skip(offset).Take(maxCount).Select(a =>
                {
                    var job = _jobFactory.CreateJob(a.Job);
                    return new QueueItem
                    {
                        Key = a.Job.Key.ToString(),
                        JobType = job?.Name,
                        Description = job?.Description.formatMessage(),
                        Running = true,
                        StartTime = a.StartTime
                    };
                }).OrderBy(a => a.StartTime));
            }
        }

        var jobs = await Delegate.SelectJobs(conn, _typeLoadHelper, maxCount - result.Count, offset, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30), MisfireTime, types,
            cancellationToken);
        var excluded = types.TypesToExclude.ToHashSet();
        var remainingCount = types.TypesToLimit;
        result.AddRange(jobs.Select(a =>
        {
            var blocked = excluded.Contains(a.JobType);
            if (!blocked && remainingCount.TryGetValue(a.JobType, out var remaining))
            {
                if (remaining == 0) blocked = true;
                else remainingCount[a.JobType] = remaining - 1;
            }

            var job = _jobFactory.CreateJob(a);
            return new QueueItem
            {
                Key = a.Key.ToString(), JobType = job?.Name, Description = job?.Description.formatMessage(), Blocked = blocked
            };
        }));

        return result;
    }

    protected override async Task TriggeredJobComplete(ConnectionAndTransactionHolder conn, IOperableTrigger trigger, IJobDetail jobDetail, SchedulerInstruction triggerInstCode,
        CancellationToken cancellationToken = new CancellationToken())
    {
        await base.TriggeredJobComplete(conn, trigger, jobDetail, triggerInstCode, cancellationToken);

        if (!jobDetail.JobType.GetCustomAttributes().Any(a =>
                _typeConcurrencyCache.ContainsKey(jobDetail.JobType) ||
                a is DisallowConcurrencyGroupAttribute or LimitConcurrencyAttribute or DisallowConcurrentExecutionAttribute))
        {
            await JobCompletedQueueEvents(conn, jobDetail, cancellationToken);
            return;
        }

        try
        {
            var types = new[]
            {
                jobDetail.JobType
            };
            if (jobDetail.JobType.GetCustomAttributes().FirstOrDefault(a => a is DisallowConcurrencyGroupAttribute) is DisallowConcurrencyGroupAttribute
                concurrencyAttribute)
            {
                types = _concurrencyGroupCache[concurrencyAttribute.Group];
            }

            await Delegate.UpdateTriggerStatesForJobFromOtherState(conn, types, StateWaiting, StateBlocked, cancellationToken);
            await Delegate.UpdateTriggerStatesForJobFromOtherState(conn, types, StatePaused, StatePausedBlocked, cancellationToken);
        }
        catch (Exception e)
        {
            throw new JobPersistenceException("Couldn't update states of blocked triggers: " + e.Message, e);
        }

        await JobCompletedQueueEvents(conn, jobDetail, cancellationToken);
    }

    private async Task<int> GetThreadPoolSize(CancellationToken cancellationToken)
    {
        var schedulerFactory = Utils.ServiceContainer.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        var metadata = await scheduler.GetMetaData(cancellationToken);
        return metadata.ThreadPoolSize;
    }

    private async Task JobStoringQueueEvents(ConnectionAndTransactionHolder conn, IJobDetail newJob, CancellationToken cancellationToken)
    {
        try
        {
            var waitingTriggerCount = await GetWaitingTriggersCount(conn, cancellationToken);
            var blockedTriggerCount = await GetBlockedTriggersCount(conn, cancellationToken);
            if (_threadPoolSize == 0) _threadPoolSize = await GetThreadPoolSize(cancellationToken);
            QueueItem[] executing;
            lock (_executingJobs)
                executing = _executingJobs.Values.Select(a =>
                {
                    var job = _jobFactory.CreateJob(a.Job);
                    return new QueueItem
                    {
                        Key = a.Job.Key.ToString(),
                        JobType = job?.Name,
                        Description = job?.Description.formatMessage(),
                        Running = true,
                        StartTime = a.StartTime
                    };
                }).OrderBy(a => a.StartTime).ToArray();

            _queueStateEventHandler.OnJobAdded(newJob, new QueueStateContext
            {
                ThreadCount = _threadPoolSize,
                WaitingTriggersCount = waitingTriggerCount,
                BlockedTriggersCount = blockedTriggerCount,
                TotalTriggersCount = waitingTriggerCount + blockedTriggerCount + executing.Length,
                CurrentlyExecuting = executing
            });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "An error occurred while firing Job Added events");
        }
    }

    private async Task JobFiringQueueEvents(ConnectionAndTransactionHolder conn, IOperableTrigger trigger, IJobDetail jobDetail,
        CancellationToken cancellationToken)
    {
        try
        {
            lock(_executingJobs) _executingJobs[jobDetail.Key] = (jobDetail, trigger.StartTimeUtc.LocalDateTime);
            var waitingTriggerCount = await GetWaitingTriggersCount(conn, cancellationToken);
            var blockedTriggerCount = await GetBlockedTriggersCount(conn, cancellationToken);
            if (_threadPoolSize == 0) _threadPoolSize = await GetThreadPoolSize(cancellationToken);
            QueueItem[] executing;
            lock (_executingJobs)
                executing = _executingJobs.Select(a =>
                {
                    var job = _jobFactory.CreateJob(a.Value.Job);
                    return new QueueItem
                    {
                        Key = a.Key.ToString(),
                        JobType = job?.Name ?? a.Value.Job.JobType.Name,
                        Description = job?.Description.formatMessage(),
                        Running = true,
                        StartTime = trigger.StartTimeUtc.LocalDateTime
                    };
                }).OrderBy(a => a.StartTime).ToArray();

            _queueStateEventHandler.OnJobExecuting(jobDetail, new QueueStateContext
            {
                ThreadCount = _threadPoolSize,
                WaitingTriggersCount = waitingTriggerCount,
                BlockedTriggersCount = blockedTriggerCount,
                TotalTriggersCount = waitingTriggerCount + blockedTriggerCount + executing.Length,
                CurrentlyExecuting = executing
            });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "An error occurred while firing Job Executing events");
        }
    }

    private async Task JobCompletedQueueEvents(ConnectionAndTransactionHolder conn, IJobDetail jobDetail, CancellationToken cancellationToken)
    {
        try
        {
            // this runs before the states have been updated, so things that were blocked for concurrency are still blocked at this point
            lock(_executingJobs) _executingJobs.Remove(jobDetail.Key);
            var waitingTriggerCount = await GetWaitingTriggersCount(conn, cancellationToken);
            var blockedTriggerCount = await GetBlockedTriggersCount(conn, cancellationToken);
            if (_threadPoolSize == 0) _threadPoolSize = await GetThreadPoolSize(cancellationToken);
            QueueItem[] executing;
            lock (_executingJobs)
                executing = _executingJobs.Values.Select(a =>
                {
                    var job = _jobFactory.CreateJob(a.Job);
                    return new QueueItem
                    {
                        Key = a.Job.Key.ToString(),
                        JobType = job?.Name,
                        Description = job?.Description.formatMessage(),
                        Running = true,
                        StartTime = a.StartTime
                    };
                }).OrderBy(a => a.StartTime).ToArray();

            _queueStateEventHandler.OnJobCompleted(jobDetail, new QueueStateContext
            {
                ThreadCount = _threadPoolSize,
                WaitingTriggersCount = waitingTriggerCount,
                BlockedTriggersCount = blockedTriggerCount,
                TotalTriggersCount = waitingTriggerCount + blockedTriggerCount + executing.Length,
                CurrentlyExecuting = executing
            });

            // this will prevent the idle waiting that exists to prevent constantly checking if it's time to trigger a schedule
            if (waitingTriggerCount > 0 || blockedTriggerCount > 0)
                SignalSchedulingChangeImmediately(new DateTimeOffset(1982, 6, 28, 0, 0, 0, TimeSpan.FromSeconds(0)));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "An error occurred while firing Job Completed events");
        }
    }

    private new IFilteredDriverDelegate Delegate => base.Delegate as IFilteredDriverDelegate;
}
