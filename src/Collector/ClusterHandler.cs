using agent;
using MongoDB.Bson;
using MongoDB.Driver;
using monitoringexe;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Collector
{
    /// <summary>
    /// Responsible for analysing the configuration of a MongoDB cluster (from single node to replica set) and instanciate one poller per node to monitor.
    /// </summary>
    public class ClusterHandler : IDisposable
    {
        private static Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly Configuration Configuration;
        private readonly String ConnectionString;

        // Name, poller
        private readonly Dictionary<String, MongodPoller> pollers = new Dictionary<String, MongodPoller>();

        // Configuration refresh poller
        private Timer Timer;

        internal ClusterHandler(Configuration cfg, String cnx)
        {
            Configuration = cfg;
            ConnectionString = cnx;

            Timer = new Timer(SyncPollersSafe, null, 0, Configuration.RefreshClusterTopologyMinute * 60 * 1000);
        }

        private void SyncPollersSafe(object data)
        {
            try
            {
                SyncPollers();
            }
            catch (Exception e)
            {
                Logger.Error(e, "Could not discover topology of MongoDb cluster at {0} - will retry later", this.ConnectionString);
            }
        }

        private void SyncPollers()
        {
            Logger.Info("Analysing MongoDB cluster at {0}", ConnectionString);

            var ClusterAnalysisClient = new MongoClient(ConnectionString);
            IMongoDatabase tmp = ClusterAnalysisClient.GetDatabase("admin");

            var isMaster = tmp.RunCommand(new BsonDocumentCommand<BsonDocument>(new BsonDocument("isMaster", 1)));
            if (!isMaster.Contains("hosts"))
            {
                // Not a replica set. Just a single node.
                Logger.Info("Single node configuration");
                String nodeName = "singlenode";
                if (!pollers.ContainsKey(nodeName))
                {
                    var y = new MongodPoller(Configuration, ClusterAnalysisClient);
                    pollers.Add(nodeName, y);
                }
            }
            else
            {
                // Replica set: create a poller per node.
                Logger.Info("Replica set configuration");
                var foundNames = new List<String>();

                String auth = null;
                if (ConnectionString.Split('@').Length == 2)
                {
                    auth = ConnectionString.Split('@')[0].Replace("mongodb://", "");
                }
                var rsStatus = tmp.RunCommand(new BsonDocumentCommand<BsonDocument>(new BsonDocument("replSetGetStatus", 1))); // must run against admin
                foreach (var member in rsStatus["members"].AsBsonArray)
                {
                    String nodeName = member["name"].AsString;
                    foundNames.Add(nodeName);

                    if (!pollers.ContainsKey(nodeName))
                    {
                        String conStr = String.Format("mongodb://{0}?wtimeout=5000&journal=false&connect=direct", nodeName);
                        if (auth != null)
                        {
                            conStr = String.Format("mongodb://{0}@{1}?wtimeout=5000&journal=false&connect=direct", auth, nodeName);
                        }
                        var y = new MongodPoller(Configuration, conStr); // "name" is actually host:port.
                        pollers.Add(nodeName, y);
                    }
                }

                foreach (String name in pollers.Keys.Where(k => !foundNames.Contains(k)).ToList()) //To list : create a copy.
                {
                    Logger.Info("Removing poller for a node which has exited the replica set: {0}", name);
                    pollers[name].Stop();
                    pollers.Remove(name);
                }
            }
        }

        public void Dispose()
        {
            Timer.Dispose();
            Timer = null;
            foreach (var poller in pollers.Values)
            {
                poller.Stop();
            }
            pollers.Clear();
        }
    }
}
