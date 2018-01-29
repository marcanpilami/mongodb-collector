using agent;
using MongoDB.Bson;
using MongoDB.Driver;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace monitoringexe
{
    /// <summary>
    /// Responsible for regularly fetching the data from a single MongoD instance and inserting it in the database.
    /// </summary>
    class MongodPoller
    {
        private static Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly Configuration Cfg;
        private readonly Timer t;

        public MongoClient ClientAnalysis { get; set; }
        public IMongoDatabase AnalysisDatabase { get; }
        public IMongoDatabase AnalysisAdminDatabase { get; }

        public MongoClient ClientTarget { get; set; }
        public IMongoDatabase TargetDatabase { get; }

        private BsonDocument PreviousTop, PreviousServerStatus;

        private readonly IMongoCollection<BsonDocument> PerfSummaryCollection, PerfDetailCollection, ProfilerCollection;

        private readonly String DbName, Hostname;

        private readonly HashSet<String> unsupported = new HashSet<string>();

        private readonly BsonDocument emptyArray;

        private bool firstLoop = true, secondLoop = true;

        private DateTime latestProfileLoopTime = DateTime.Now;
        private DateTime startOfLoopDatabaseTime;

        public MongodPoller(Configuration cfg, MongoClient client)
        {
            this.Hostname = client.Settings.Server.Host + ":" + client.Settings.Server.Port; // remove port?
            Logger.Info("Starting monitoring for database instance {0}", this.Hostname);

            this.Cfg = cfg;
            this.ClientAnalysis = client;
            this.AnalysisDatabase = ClientAnalysis.GetDatabase("admin");
            this.AnalysisAdminDatabase = ClientAnalysis.GetDatabase("admin");
            this.DbName = "admin";

            var master = AnalysisDatabase.RunCommand(new BsonDocumentCommand<BsonDocument>(new BsonDocument("isMaster", 1)));
            Logger.Info("Monitored database is master: {0}", master["ismaster"].AsBoolean);
            if (cfg.ResultsStorageConnection == null && master["ismaster"].AsBoolean)
            {
                TargetDatabase = AnalysisDatabase;
                Logger.Info("All data will be stored inside the monitored database itself");
            }
            else if (cfg.ResultsStorageConnection == null)
            {
                ClientTarget = new MongoClient("mongodb://" + master["primary"].AsString + "?replicaSet=" + master["setName"]);
                TargetDatabase = ClientTarget.GetDatabase(this.DbName);
                Logger.Info("All data will be stored inside the monitored database itself, but as the monitored db is a slave replica, all data will be sent to instance {0} instead", master["primary"].AsString);
            }
            else if (cfg.ResultsStorageConnection != null)
            {
                ClientTarget = new MongoClient(cfg.ResultsStorageConnection.ConnectionString);
                TargetDatabase = ClientTarget.GetDatabase(cfg.ResultsStorageConnection.DatabaseName);
                Logger.Info("All data will be sent to a specific database");
            }

            // Collections to store result data
            if (!(TargetDatabase.ListCollections(new ListCollectionsOptions { Filter = new BsonDocument("name", cfg.DetailCollectionName) }).ToList()).Any())
            {
                TargetDatabase.CreateCollection(cfg.DetailCollectionName, new CreateCollectionOptions { Capped = true, MaxSize = cfg.DetailCollectionMaxSizeMb * 1024 * 1024 });
            }
            PerfSummaryCollection = TargetDatabase.GetCollection<BsonDocument>(cfg.SummaryCollectionName);
            PerfDetailCollection = TargetDatabase.GetCollection<BsonDocument>(cfg.DetailCollectionName);

            if (!TargetDatabase.RunCommand(new BsonDocumentCommand<BsonDocument>(new BsonDocument("collstats", cfg.DetailCollectionName)))["capped"].AsBoolean)
            {
                TargetDatabase.RunCommand(new BsonDocumentCommand<BsonDocument>(new BsonDocument(new Dictionary<string, object> { { "convertToCapped", cfg.DetailCollectionName }, { "size", cfg.DetailCollectionMaxSizeMb * 1024 * 1024 } })));
            }
            PerfDetailCollection.Indexes.CreateOne(Builders<BsonDocument>.IndexKeys.Ascending(d => d["node"]).Ascending(d => d["key"]).Ascending(d => d["when"]), new CreateIndexOptions { Unique = true });

            if (!(TargetDatabase.ListCollections(new ListCollectionsOptions { Filter = new BsonDocument("name", cfg.ProfilerCollectionName) }).ToList()).Any())
            {
                TargetDatabase.CreateCollection(cfg.ProfilerCollectionName, new CreateCollectionOptions { Capped = true, MaxSize = cfg.ProfilerCollectionMaxSizeMb * 1024 * 1024 });
            }
            ProfilerCollection = TargetDatabase.GetCollection<BsonDocument>(cfg.ProfilerCollectionName);
            ProfilerCollection.Indexes.CreateOne(Builders<BsonDocument>.IndexKeys.Ascending("ns").Ascending("ts"));

            // The empty array must be initialized according to the polling period
            emptyArray = new BsonDocument();
            for (int idx = 0; idx < 60; idx++)
            {
                emptyArray[idx.ToString()] = new BsonInt64(-1);
            }

            // Go
            t = new Timer(PollSafe, null, 0, Cfg.RefreshPeriodSecond * 1000);
        }

        public MongodPoller(Configuration cfg, String conStr) : this(cfg, new MongoClient(conStr))
        { }

        internal void Stop()
        {
            t.Dispose();
        }

        private void PollSafe(object data)
        {
            try
            {
                Poll(data).Wait();
            }
            catch (Exception e)
            {
                Logger.Error(e, "could not poll database " + this.Hostname + ".");
            }
        }

        private async Task<BsonDocument> GetAllDatabaseStats()
        {
            var dbs = await AnalysisAdminDatabase.RunCommandAsync((new BsonDocumentCommand<BsonDocument>(new BsonDocument("listDatabases", 1))));
            BsonDocument res = null;
            List<BsonDocument> longQueriesAllDbs = new List<BsonDocument>();
            foreach (var db in dbs["databases"].AsBsonArray)
            {
                if (new string[] { "local", "admin" }.Any(s => s == db["name"].AsString))
                {
                    continue;
                }

                IMongoDatabase idb = ClientAnalysis.GetDatabase(db["name"].AsString);
                var wait = idb.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("dbStats", 1)));
                var longQueries = idb.GetCollection<BsonDocument>("system.profile").Find(
                    Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Gte("ts", latestProfileLoopTime),
                        Builders<BsonDocument>.Filter.Lt("ts", startOfLoopDatabaseTime))).ToListAsync();

                longQueriesAllDbs.AddRange((await longQueries).Where(d => !d.Contains("ns") ||
                        !(d["ns"].AsString.EndsWith("system.profile") || d["ns"].AsString.Contains(Cfg.ProfilerCollectionName) ||
                        d["ns"].AsString.Contains(Cfg.DetailCollectionName) || d["ns"].AsString.Contains(Cfg.SummaryCollectionName) ||
                        (d.Contains("op") && d["op"].AsString == "command"))));
                var stats = await wait;

                if (res == null)
                {
                    res = stats.AsBsonDocument;
                }
                else
                {
                    foreach (var key in stats.AsBsonDocument.Where(k => k.Name != "db"))
                    {
                        res[key.Name] = res[key.Name].ToLong() + key.Value.ToLong();
                    }
                }
            }
            latestProfileLoopTime = startOfLoopDatabaseTime;

            foreach (var d in longQueriesAllDbs)
            {
                // _id is forbidden!
                d.Remove("_id");
                // Also, fields beginning with $ are forbidden... stupid.
                EscapeDollar(d);
            }

            if (res != null)
                res["profile"] = new BsonArray(longQueriesAllDbs);
            return res;
        }

        private void EscapeDollar(BsonDocument doc)
        {
            foreach (var key in doc.Names.ToList())
            {
                var value = doc[key];
                if (key.Contains("$") || key.Contains("."))
                {
                    doc.Remove(key);
                    doc.Add(key.Replace("$", "").Replace(".", ""), value);
                }

                if (value.IsBsonDocument)
                {
                    EscapeDollar(value.AsBsonDocument);
                }
                if (value.IsBsonArray)
                {
                    foreach (var it in value.AsBsonArray)
                    {
                        if (it.IsBsonDocument)
                        {
                            EscapeDollar(it.AsBsonDocument);
                        }
                    }
                }
            }
        }

        private async Task Poll(object data)
        {
            MappedDiagnosticsContext.Set("Host", this.Hostname);
            MappedDiagnosticsContext.Set("Db", this.DbName);

            // First: get the data.
            var loopTime = DateTime.Now;
            var loopTimeHour = loopTime.AddMinutes(-loopTime.Minute).AddSeconds(-loopTime.Second).AddMilliseconds(-loopTime.Millisecond);
            BsonDocument newTop = await AnalysisAdminDatabase.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("top", 1)));
            BsonDocument newServerStatus = await AnalysisDatabase.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("serverStatus", 1)));
            startOfLoopDatabaseTime = newServerStatus["localTime"].AsDateTime;
            BsonDocument newDatabaseStats = await GetAllDatabaseStats();
            BsonDocument master = AnalysisDatabase.RunCommand(new BsonDocumentCommand<BsonDocument>(new BsonDocument("isMaster", 1)));
            BsonDocument newReplicaSetStatus = null;

            if (master.Contains("hosts"))
            {
                newReplicaSetStatus = await AnalysisAdminDatabase.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("replSetGetStatus", 1)));
                newReplicaSetStatus = newReplicaSetStatus["members"].AsBsonArray.First(b => b.AsBsonDocument.Contains("self") && b["self"].AsBoolean).AsBsonDocument;
            }

            var updateSummary = Builders<BsonDocument>.Update.CurrentDate("_measure").Set("node", ClientAnalysis.Settings.Server.Host);

            var updatesDetail = new List<WriteModel<BsonDocument>>();
            var insertsDetail = new List<WriteModel<BsonDocument>>();
            BsonValue tmpNew, tmpOld;
            foreach (Item i in Cfg.Items)
            {
                switch (i.Root)
                {
                    case "serverStatus":
                        tmpNew = newServerStatus;
                        tmpOld = PreviousServerStatus;
                        break;
                    case "top":
                        tmpNew = newTop;
                        tmpOld = PreviousTop;
                        break;
                    case "dbStats":
                        tmpOld = null;
                        tmpNew = newDatabaseStats;
                        break;
                    case "rsStatus":
                        tmpOld = null;
                        tmpNew = newReplicaSetStatus;
                        break;
                    default:
                        Logger.Warn("unknown path root {0}, item [{1}] will be ignored", i.Root, i.Path);
                        continue;
                }

                long res = 0;
                try
                {
                    foreach (string s in i.PathSegments)
                    {
                        tmpNew = tmpNew[s];
                    }
                    res = tmpNew.ToLong();

                    // If diff mode, substract the previous result if any (ignore if no previous result).
                    if (i.Mode == ItemMode.diff && tmpOld == null)
                    {
                        // First loop
                        continue;
                    }
                    if (i.Mode == ItemMode.diff)
                    {
                        foreach (string s in i.PathSegments)
                        {
                            tmpOld = tmpOld[s];
                        }
                        res -= tmpOld.ToLong();
                    }

                    // Multiply if needed
                    res = (long)(res * i.Multiplier);

                    // Remove from unsupported if needed
                    if (unsupported.Contains(i.Path))
                    {
                        Logger.Info("Item {0} has become supported again", i.Path);
                        unsupported.Remove(i.Path);
                    }
                }
                catch (Exception ex)
                {
                    // Items may not exist yet, because nothing has happened (empty value sometime miss)
                    if (!unsupported.Contains(i.Path))
                    {
                        Logger.Warn("Item {0} has become unsupported as it has failed with error {1}. It may be normal - some items are not present until set by the DB.", i.Path, ex.Message);
                        unsupported.Add(i.Path);
                    }
                    continue;
                }
                Logger.Debug("Item {0} is: {1}", i.Path, res);

                if (i.InSummary)
                {
                    updateSummary = updateSummary.Set(i.JsonUniqueName, res);
                }

                if (firstLoop || secondLoop || loopTime.Minute == 00)
                {
                    var nn = new BsonDocument();
                    nn["node"] = ClientAnalysis.Settings.Server.Host;
                    nn["key"] = i.JsonUniqueName;
                    nn["when"] = loopTimeHour;
                    nn["values"] = emptyArray;

                    var ins = new InsertOneModel<BsonDocument>(nn);
                    insertsDetail.Add(ins);
                }

                var op = new UpdateOneModel<BsonDocument>(Builders<BsonDocument>.Filter.Where(
                        d => d["node"] == ClientAnalysis.Settings.Server.Host && d["key"] == i.JsonUniqueName && d["when"] == loopTimeHour),
                        Builders<BsonDocument>.Update.
                            //SetOnInsert(j => j["values"], emptyArray).
                            Set(j => j["values"][loopTime.Minute], res)
                        );
                //op.IsUpsert = true;
                updatesDetail.Add(op);
            }

            // Special hardcoded items
            updateSummary = updateSummary.Set("node_version", newServerStatus["version"]);
            updateSummary = updateSummary.Set("node_master", master["ismaster"].AsBoolean);
            updateSummary = updateSummary.Set("host_name", newServerStatus["host"]);
            updateSummary = updateSummary.Set("host_fqdn", newServerStatus.Contains("advisoryHostFQDNs") && newServerStatus["advisoryHostFQDNs"] != null ? newServerStatus["advisoryHostFQDNs"].AsBsonArray.Count > 0 ? newServerStatus["advisoryHostFQDNs"][0] : "localhost" : "localhost");

            // End of loop - current items become previous ones, and run the update query.
            PreviousServerStatus = newServerStatus;
            PreviousTop = newTop;

            if (insertsDetail.Count > 0)
            {
                try
                {
                    await PerfDetailCollection.BulkWriteAsync(insertsDetail, new BulkWriteOptions { IsOrdered = false });
                }
                catch (MongoBulkWriteException)
                {
                    // Ignore - duplicate keys can happen on startup.
                }
            }
            List<Task> tasks = new List<Task>(3);
            tasks.Add(PerfDetailCollection.BulkWriteAsync(updatesDetail, new BulkWriteOptions { IsOrdered = false }));
            tasks.Add(PerfSummaryCollection.UpdateOneAsync(j => j["node"] == ClientAnalysis.Settings.Server.Host, updateSummary, new UpdateOptions { IsUpsert = true }));
            if (newDatabaseStats["profile"].AsBsonArray.Count > 0)
            {
                tasks.Add(ProfilerCollection.InsertManyAsync(newDatabaseStats["profile"].AsBsonArray.Values.OfType<BsonDocument>(), new InsertManyOptions { BypassDocumentValidation = true }));
            }
            Task.WaitAll(tasks.ToArray());

            if (!firstLoop)
            { secondLoop = false; }
            firstLoop = false;
        }
    }
}
