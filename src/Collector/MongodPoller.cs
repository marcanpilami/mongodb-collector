using MongoDB.Bson;
using MongoDB.Driver;
using monitoringexe;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

        private readonly IMongoCollection<BsonDocument> PerfSummaryCollection, PerfDetailCollection;

        private readonly String DbName, Hostname;

        private readonly HashSet<String> unsupported = new HashSet<string>();

        private readonly BsonDocument emptyArray;

        private bool firstLoop = true, secondLoop = true;

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
            if (cfg.TargetConnection == null && master["ismaster"].AsBoolean)
            {
                TargetDatabase = AnalysisDatabase;
                Logger.Info("All data will be stored inside the monitored database itself");
            }
            else if (cfg.TargetConnection == null)
            {
                ClientTarget = new MongoClient("mongodb://" + master["primary"].AsString + "?replicaSet=" + master["setName"]);
                TargetDatabase = ClientTarget.GetDatabase(this.DbName);
                Logger.Info("All data will be stored inside the monitored database itself, but as the monitored db is a slave replica, all data will be sent to instance {0} instead", master["primary"].AsString);
            }
            else if (cfg.TargetConnection != null)
            {
                ClientTarget = new MongoClient(cfg.TargetConnection.ConnectionString);
                TargetDatabase = ClientTarget.GetDatabase(cfg.TargetConnection.DatabaseName);
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

            // The empty array must be initialized according to the polling period
            emptyArray = new BsonDocument();
            for (int idx = 0; idx < 60; idx++)
            {
                emptyArray[idx.ToString()] = new BsonInt64(-1);
            }

            // Go
            t = new Timer(Poll, null, 0, Cfg.RefreshPeriodSecond * 1000);
        }

        public MongodPoller(Configuration cfg, String hostAndPort) : this(cfg, new MongoClient(String.Format("mongodb://{0}?wtimeout=5000&journal=false&connect=direct", hostAndPort)))
        { }

        internal void Stop()
        {
            t.Dispose();
        }

        private async Task<BsonDocument> GetAllDatabaseStats()
        {
            var dbs = await AnalysisAdminDatabase.RunCommandAsync((new BsonDocumentCommand<BsonDocument>(new BsonDocument("listDatabases", 1))));
            BsonDocument res = null;
            foreach (var db in dbs["databases"].AsBsonArray)
            {
                IMongoDatabase idb = ClientAnalysis.GetDatabase(db["name"].AsString);
                var stats = await idb.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("dbStats", 1)));

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
            return res;
        }

        private async void Poll(object data)
        {
            MappedDiagnosticsContext.Set("Host", this.Hostname);
            MappedDiagnosticsContext.Set("Db", this.DbName);

            // First: get the data.
            var loopTime = DateTime.Now;
            var loopTimeHour = loopTime.AddMinutes(-loopTime.Minute).AddSeconds(-loopTime.Second).AddMilliseconds(-loopTime.Millisecond);
            BsonDocument newTop = await AnalysisAdminDatabase.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("top", 1)));
            BsonDocument newServerStatus = await AnalysisDatabase.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("serverStatus", 1)));
            BsonDocument newDatabaseStats = await GetAllDatabaseStats();
            BsonDocument master = AnalysisDatabase.RunCommand(new BsonDocumentCommand<BsonDocument>(new BsonDocument("isMaster", 1)));

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
            updateSummary = updateSummary.Set("host_fqdn", newServerStatus["advisoryHostFQDNs"] != null ? newServerStatus["advisoryHostFQDNs"].AsBsonArray.Count > 0 ? newServerStatus["advisoryHostFQDNs"][0] : "localhost" : "localhost");

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
            await PerfDetailCollection.BulkWriteAsync(updatesDetail, new BulkWriteOptions { IsOrdered = false });
            await PerfSummaryCollection.UpdateOneAsync(j => j["node"] == ClientAnalysis.Settings.Server.Host, updateSummary, new UpdateOptions { IsUpsert = true });


            if (!firstLoop)
            { secondLoop = false; }
            firstLoop = false;
        }
    }
}

