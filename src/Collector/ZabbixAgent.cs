using MongoDB.Bson;
using MongoDB.Driver;
using monitoringexe;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using NLog;

namespace agent.zabbix
{
    internal class ZabbixAgent : IDisposable
    {
        private static Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly TcpListener listener;
        private readonly Configuration cfg;

        private readonly Dictionary<string, MongoClient> Connexions = new Dictionary<string, MongoClient>();
        private readonly Dictionary<string, IMongoDatabase> Databases = new Dictionary<string, IMongoDatabase>();
        private readonly Dictionary<Tuple<IMongoDatabase, string>, Tuple<DateTime, BsonValue>> DataCache = new Dictionary<Tuple<IMongoDatabase, string>, Tuple<DateTime, BsonValue>>();

        private long Pending = 0;

        internal ZabbixAgent(Configuration cfg)
        {
            Logger.Info("Starting Zabbix MongoDB agent");
            try
            {
                this.cfg = cfg;
                IPAddress addr;
                if (cfg.ZabbixAgentListeningInterface == "0.0.0.0")
                {
                    addr = IPAddress.Any;
                }
                else
                {
                    addr = Dns.GetHostAddressesAsync(cfg.ZabbixAgentListeningInterface).Result.First();
                }

                listener = new TcpListener(addr, cfg.ZabbixAgentListeningPort);
                listener.Server.ReceiveTimeout = 10000;
                listener.Server.SendTimeout = 10000;
                listener.Start();
                Task.Run(AcceptClients); // New thread.
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Zabbix agent has failed to start");
            }
        }

        public void Dispose()
        {
            Logger.Info("Zabbix MongoDB is stopping");
            listener.Stop();
        }

        private async Task AcceptClients()
        {
            Logger.Info("Zabbix MongoDB agent is now open for connections");
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                DoWork(client);
            }
        }

        private async Task DoWork(TcpClient client)
        {
            var watch = Stopwatch.StartNew();
            var buf = new byte[4096];
            client.ReceiveTimeout = 10000;
            client.SendTimeout = 10000;
            var stream = client.GetStream();

            try
            {
                Logger.Trace("New request received. Pending: {0}", ++Pending);
                String firstLine = "";
                String after = "";
                while (true)
                {
                    var read = await stream.ReadAsync(buf, 0, buf.Length);

                    if (read == 0) break; //end of stream.
                    var s = System.Text.Encoding.ASCII.GetString(buf, 0, read);
                    if (s.Contains("\n"))
                    {
                        firstLine += s.Split('\n')[0];
                        if (s.Split('\n').Length > 1)
                        {
                            after = s.Split('\n')[1];
                        }
                        break;
                    }
                    firstLine += s;
                }

                if (firstLine.StartsWith("ZBXD"))
                {
                    firstLine = firstLine.Substring(6); // Remove optional ZBDX + version (0x1) + ACK (0x6) header
                }
                var request = firstLine.Trim().Replace("\0", "");
                Logger.Trace(request);

                // Extract arguments. Request should be in the form key[hostkey, database_name] - with arguments being optional.
                // Example: lock_collection_exclusive_intent_deadlock_count[192.168.13.54:27017, marsu_db]
                var key = request.Split('[')[0];
                var args = request.Split('[').Length > 1 ? request.Split('[')[1].Replace("]", "") : null;
                string host_key = null, db_name = null;
                if (args != null)
                {
                    host_key = args.Split(',')[0].Trim();
                    db_name = args.Split(',').Length > 1 ? args.Split(',')[1].Trim() : null;
                }

                // Handle special cases
                if (key == "mongoagent.ping")
                {
                    await SendResult("1", stream);
                    client.Dispose();
                    return;
                }
                if (key == "mongoagent.discover.node")
                {
                    await SendResult(await DiscoverHosts(), stream);
                    client.Dispose();
                    return;
                }
                if (key == "mongoagent.discover.database")
                {
                    await SendResult(await DiscoverDatabases(), stream);
                    client.Dispose();
                    return;
                }

                // Does the key exist?
                var item = this.cfg.Items.SingleOrDefault(i => i.JsonUniqueName == key);
                if (item == null)
                {
                    await SendNotSupported("no item named " + key + " in configuration", stream);
                    client.Dispose();
                    return;
                }

                // Resolve the request
                long res = 0;
                BsonValue rqRes;
                var db = await GetDatabase(host_key, db_name ?? "admin");
                try
                {
                    rqRes = await GetRootDocument(item.Root, db);
                }
                catch (ZabbixClientException e)
                {
                    await SendNotSupported("Item " + item.Path + ": " + e.Message, stream);
                    client.Dispose();
                    return;
                }

                foreach (string s in item.PathSegments)
                {
                    rqRes = rqRes[s];
                }
                res = rqRes.ToLong();

                // Multiply if needed
                res = (long)(res * item.Multiplier);

                // Done
                await SendResult(res.ToString(), stream);
            }
            catch (Exception ex)
            {
                Logger.Trace(ex, "Issue when serving request");
                try
                {
                    await SendNotSupported("item could not be resolved " + ex.Message, stream);
                }
                catch (Exception)
                {
                    // Just give up.
                }
            }
            finally
            {
                try
                {
                    client.Dispose();
                }
                catch (Exception)
                {
                    // Do nothing. A socket which cannot be closed is likely not open anyway!
                }
                watch.Stop();
                Logger.Trace("Query served in {0} ms. Pending: {1}", watch.ElapsedMilliseconds, --Pending);
            }
        }

        private async Task<BsonValue> GetRootDocument(String root, IMongoDatabase db)
        {
            BsonValue rqRes = null;

            // Check in cache
            var key = Tuple.Create(db, root);
            if (DataCache.ContainsKey(key) && DataCache[key].Item1 > DateTime.Now.AddSeconds(-cfg.ZabbixAgentQueryCacheSecond))
            {
                Logger.Trace("Result from cache");
                return DataCache[key].Item2;
            }

            // Otherwise, fetch it from database.
            switch (root)
            {
                case "serverStatus":
                    rqRes = await db.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("serverStatus", 1)));
                    break;
                case "top":
                    rqRes = await db.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("top", 1)));
                    break;
                case "dbStats":
                    rqRes = await db.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("dbStats", 1)));
                    break;
                case "rsStatus":
                    BsonDocument master = db.RunCommand(new BsonDocumentCommand<BsonDocument>(new BsonDocument("isMaster", 1)));
                    if (master.Contains("hosts"))
                    {
                        // This is a replica set, so this query is allowed
                        rqRes = await db.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("replSetGetStatus", 1)));
                        rqRes = rqRes["members"].AsBsonArray.First(b => b.AsBsonDocument.Contains("self") && b["self"].AsBoolean).AsBsonDocument;
                    }
                    else
                    {
                        // This is not a replica set. This item cannot be supported.
                        throw new ZabbixClientException("item uses replica set status but the database is a single instance");
                    }
                    break;
                default:
                    throw new ZabbixClientException("item is wrong in agent configuration (non-existent root)");
            }

            // Cache it and go.
            DataCache[key] = Tuple.Create(DateTime.Now, rqRes);
            return rqRes;
        }

        private async Task SendNotSupported(String reason, NetworkStream stream)
        {
            //Logger.Trace("Not supported {0}", reason);
            await SendResult("ZBX_NOT_SUPPORTED\0" + reason, stream);
        }

        private async Task SendResult(String res, NetworkStream stream)
        {
            Logger.Trace("Result is {0}", res);
            var bb = new byte[System.Text.Encoding.ASCII.GetByteCount(res)];
            System.Text.Encoding.ASCII.GetBytes(res, 0, res.Length, bb, 0);

            await stream.WriteAsync(new byte[] { 0x5a, 0x42, 0x58, 0x44, 0x01 }, 0, 5);
            await stream.WriteAsync(LongToLittleEndianArray(bb.Length), 0, 8);
            await stream.WriteAsync(bb, 0, bb.Length);
        }

        private byte[] LongToLittleEndianArray(long data)
        {
            byte[] b = new byte[8];
            b[0] = (byte)data;
            b[1] = (byte)(((ulong)data >> 8) & 0xFF);
            b[2] = (byte)(((ulong)data >> 16) & 0xFF);
            b[3] = (byte)(((ulong)data >> 24) & 0xFF);
            b[4] = (byte)(((ulong)data >> 32) & 0xFF);
            b[5] = (byte)(((ulong)data >> 40) & 0xFF);
            b[6] = (byte)(((ulong)data >> 48) & 0xFF);
            b[7] = (byte)(((ulong)data >> 56) & 0xFF);
            return b;
        }

        private async Task<MongoClient> GetInstanceConnection(string key_cnx)
        {
            key_cnx = key_cnx.Replace("_", ":").ToLowerInvariant();

            if (this.Connexions.ContainsKey(key_cnx))
            {
                // In cache.
                return this.Connexions[key_cnx];
            }
            else
            {
                foreach (String cnxStr in cfg.MonitoredConnectionStrings)
                {
                    if (cnxStr.ToLowerInvariant().Contains(key_cnx))
                    {
                        this.Connexions[key_cnx] = new MongoClient(cnxStr.Contains("?") ? cnxStr + "&connect=direct" : cnxStr + "?connect=direct&wtimeout=5000&journal=false&connectTimeoutMS=5000&serverSelectionTimeout=5s");
                        break;
                    }

                    // It may also be a node in a replica set for which we only have one node referenced in configuration
                    // so check all replica set members for all connection strings.
                    // Costly, but only done once.
                    var cnxstr2 = cnxStr.Contains("?") ? cnxStr + "&connectTimeoutMS=5000&serverSelectionTimeout=5s" : cnxStr + "?connectTimeoutMS=5000&serverSelectionTimeout=5s";
                    var tmpClient = new MongoClient(cnxstr2);
                    IMongoDatabase tmp = tmpClient.GetDatabase("admin");

                    BsonDocument isMaster;
                    try
                    {
                        isMaster = await tmp.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("isMaster", 1)));
                    }
                    catch (TimeoutException)
                    {
                        continue; // Cannot connect to a given connection string, just go to the next one.
                    }

                    if (!isMaster.Contains("hosts"))
                    {
                        // Not a replica set. Just a single node. Means not found.
                        continue;
                    }
                    else
                    {
                        // Replica set.                    
                        var rsStatus = await tmp.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("replSetGetStatus", 1))); // must run against admin
                        foreach (var member in rsStatus["members"].AsBsonArray)
                        {
                            var name = member["name"].AsString;
                            if (name.ToLowerInvariant() == key_cnx)
                            {
                                String auth = "";
                                if (cnxStr.Split('@').Length == 2)
                                {
                                    auth = cnxStr.Split('@')[0].Replace("mongodb://", "") + "@";
                                }

                                this.Connexions[key_cnx] = new MongoClient(String.Format("mongodb://{0}{1}?wtimeout=5000&journal=false&connect=direct", auth, name));
                                break;
                            }
                        }

                        if (this.Connexions.ContainsKey(key_cnx))
                        {
                            break;
                        }
                    }
                }

                if (!this.Connexions.ContainsKey(key_cnx))
                {
                    Logger.Warn("No connection known for monitoring instance with key {0}.", key_cnx);
                    return null;
                }
                return Connexions[key_cnx];
            }
        }

        private async Task<IMongoDatabase> GetDatabase(String key_cnx, String database = "admin")
        {
            key_cnx = key_cnx.Replace("_", ":").ToLowerInvariant();
            var key_db = key_cnx + "_" + database;

            if (this.Databases.ContainsKey(key_db))
            {
                // In cache.
                return this.Databases[key_db];
            }
            else
            {
                var cnx = await GetInstanceConnection(key_cnx);
                if (cnx == null)
                {
                    return null;
                }

                this.Databases[key_db] = cnx.GetDatabase(database);
            }


            if (!this.Databases.ContainsKey(key_db))
            {
                Logger.Warn("No database known for monitoring instance with key {0}", key_cnx);
                return null;
            }
            return Databases[key_db];
        }

        private async Task<List<String>> MonitoredNodeNames()
        {
            List<String> monitored = new List<string>();
            foreach (String cnx in cfg.MonitoredConnectionStrings)
            {
                var tmpClient = new MongoClient(cnx);
                IMongoDatabase tmp = tmpClient.GetDatabase("admin");

                var isMaster = await tmp.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("isMaster", 1)));
                if (!isMaster.Contains("hosts"))
                {
                    // Not a replica set. Just a single node.
                    monitored.Add(tmpClient.Settings.Server.Host + ":" + tmpClient.Settings.Server.Port);
                }
                else
                {
                    // Replica set.                    
                    var rsStatus = await tmp.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("replSetGetStatus", 1))); // must run against admin
                    foreach (var member in rsStatus["members"].AsBsonArray)
                    {
                        monitored.Add(member["name"].AsString);
                    }
                }
            }
            return monitored;
        }

        private async Task<String> DiscoverHosts()
        {
            String res = "{ \"data\": [";
            res += string.Join(",", (await MonitoredNodeNames()).Select(c => " { \"{#NODEKEY}\":\"" + c.Replace(":", "_") + "\"}"));
            res += "]}";

            return res;
        }

        private async Task<String> DiscoverDatabases()
        {
            String res = "{ \"data\": [";
            List<String> dbs = new List<string>();

            foreach (String nodeName in await MonitoredNodeNames())
            {
                var a = await GetDatabase(nodeName); // Admin db inside instance.
                if (a == null)
                {
                    continue;
                }
                var list = await a.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("listDatabases", 1)));
                foreach (BsonValue db in list["databases"].AsBsonArray)
                {
                    dbs.Add("{ \"{#NODEKEY}\":\"" + nodeName + "\", \"{#DBNAME}\":\"" + db["name"] + "\"}");
                }
            }

            res += string.Join(",", dbs);
            res += "]}";

            return res;
        }
    }

    internal class ZabbixClientException : Exception
    {
        internal ZabbixClientException(String message) : base(message) { }
    }
}
