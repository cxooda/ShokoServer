﻿using System.Collections.Generic;
using NHibernate;
using NHibernate.Criterion;
using Shoko.Models.Server;
using Shoko.Server.Databases;

namespace Shoko.Server.Repositories.Direct;

public class AniDB_Anime_SimilarRepository : BaseDirectRepository<AniDB_Anime_Similar, int>
{
    public AniDB_Anime_Similar GetByAnimeIDAndSimilarID(int animeid, int similaranimeid)
    {
        return Lock(() =>
        {
            using var session = DatabaseFactory.SessionFactory.OpenSession();
            var cr = session
                .CreateCriteria(typeof(AniDB_Anime_Similar))
                .Add(Restrictions.Eq("AnimeID", animeid))
                .Add(Restrictions.Eq("SimilarAnimeID", similaranimeid))
                .UniqueResult<AniDB_Anime_Similar>();
            return cr;
        });
    }

    public List<AniDB_Anime_Similar> GetByAnimeID(int id)
    {
        return Lock(() =>
        {
            using var session = DatabaseFactory.SessionFactory.OpenSession();
            return GetByAnimeID(session, id);
        });
    }

    public List<AniDB_Anime_Similar> GetByAnimeID(ISession session, int id)
    {
        return Lock(() =>
        {
            var cats = session
                .CreateCriteria(typeof(AniDB_Anime_Similar))
                .Add(Restrictions.Eq("AnimeID", id))
                .AddOrder(Order.Desc("Approval"))
                .List<AniDB_Anime_Similar>();

            return new List<AniDB_Anime_Similar>(cats);
        });
    }
}
