using MongoDB.Bson;
using MongoDB.Driver;
using monitoringexe;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Collector
{
    /// <summary>
    /// Responsible for analysing the configuration of a MongoDB cluster (from single node to replica set) and instanciate one poller per node to monitor.
    /// </summary>
    public class ClusterHandler : IDisposable
    {
        private static Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly Configuration cfg;
        private readonly Connection cnx;

        private readonly List<MongodPoller> pollers = new List<MongodPoller>();

        private readonly MongoClient ClusterAnalysisClient;

        internal ClusterHandler(Configuration cfg, Connection cnx)
        {
            this.cfg = cfg;
            this.cnx = cnx;

            Logger.Info("Analysing MongoDB cluster at {0}", cnx.ConnectionString);

            ClusterAnalysisClient = new MongoClient(cnx.ConnectionString);
            IMongoDatabase tmp = ClusterAnalysisClient.GetDatabase("admin");

            var isMaster = tmp.RunCommand(new BsonDocumentCommand<BsonDocument>(new BsonDocument("isMaster", 1)));
            if (!isMaster.Contains("hosts"))
            {
                // Not a replica set. Just a single node.
                Logger.Info("Single node configuration");
                var y = new MongodPoller(cfg, ClusterAnalysisClient);
                pollers.Add(y);
            }
            else
            {
                // Replica set: create a poller per node.
                Logger.Info("Replica set configuration");

                var rsStatus = tmp.RunCommand(new BsonDocumentCommand<BsonDocument>(new BsonDocument("replSetGetStatus", 1))); // must run against admin
                foreach (var member in rsStatus["members"].AsBsonArray)
                {
                    var y = new MongodPoller(cfg, member["name"].AsString); // "name" is actually host:port.
                    pollers.Add(y);
                }
            }
        }

        public void Dispose()
        {
            foreach (var poller in pollers)
            {
                poller.Stop();
            }
        }
    }
}
