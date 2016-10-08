using MongoDB.Bson;
using MongoDB.Driver;
using monitoringexe;
using System;
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

        private readonly Dictionary<String, MongoClient> Connexions = new Dictionary<string, MongoClient>();

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
                listener.Start();
                AcceptClients(); // Fire and forget. Not an issue/
            }
            catch (Exception ex)
            {
                Logger.Warn("Zabbix agent has failed to start", ex);
            }
        }

        public void Dispose()
        {
            listener.Stop();
        }

        private async Task AcceptClients()
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                DoWork(client);
            }
        }

        private async Task DoWork(TcpClient client)
        {
            var buf = new byte[4096];
            client.ReceiveTimeout = 10000;
            client.SendTimeout = 10000;
            var stream = client.GetStream();

            try
            {
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
                    await SendResult(DiscoverHosts(), stream);
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
                var db = GetDatabase(host_key, db_name ?? "admin");
                BsonValue rqRes;
                switch (item.Root)
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
                            rqRes = await db.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("replSetGetStatus", 1)));
                            rqRes = rqRes["members"].AsBsonArray.First(b => b.AsBsonDocument.Contains("self") && b["self"].AsBoolean).AsBsonDocument;
                        }
                        else
                        {
                            await SendNotSupported("item named " + key + " uses replica set status but the database is a single instance", stream);
                            client.Dispose();
                            return;
                        }
                        break;
                    default:
                        await SendNotSupported("item named " + key + " is wrong in agent configuration (non-existent root)", stream);
                        client.Dispose();
                        return;
                }

                long res = 0;

                foreach (string s in item.PathSegments)
                {
                    rqRes = rqRes[s];
                }
                res = rqRes.ToLong();

                // Multiply if needed
                res = (long)(res * item.Multiplier);

                // Done
                await SendResult(res.ToString(), stream);
                client.Dispose();
            }
            catch (Exception ex)
            {
                await SendNotSupported("item could not be resolved " + ex.Message, stream);
                client.Dispose();
                return;
            }
        }

        private async Task SendNotSupported(String reason, NetworkStream stream)
        {
            await SendResult("ZBX_NOT_SUPPORTED\0" + reason, stream);
        }

        private async Task SendResult(String res, NetworkStream stream)
        {
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

        private IMongoDatabase GetDatabase(String key, String database = "admin")
        {
            key = key.Replace("_", ":").ToLowerInvariant();

            if (this.Connexions.ContainsKey(key))
            {
                // In cache.
                return this.Connexions[key].GetDatabase(database);
            }
            else
            {
                foreach (String cnxStr in cfg.MonitoredConnectionStrings)
                {
                    if (cnxStr.ToLowerInvariant().Contains(key))
                    {
                        this.Connexions[key] = new MongoClient(cnxStr.Contains("?") ? cnxStr + "&connect=direct" : cnxStr + "?connect=direct&wtimeout=5000&journal=false");
                        break;
                    }

                    var tmpClient = new MongoClient(cnxStr);
                    IMongoDatabase tmp = tmpClient.GetDatabase("admin");

                    BsonDocument isMaster;
                    try
                    {
                        isMaster = tmp.RunCommand(new BsonDocumentCommand<BsonDocument>(new BsonDocument("isMaster", 1)));
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
                        var rsStatus = tmp.RunCommand(new BsonDocumentCommand<BsonDocument>(new BsonDocument("replSetGetStatus", 1))); // must run against admin
                        foreach (var member in rsStatus["members"].AsBsonArray)
                        {
                            var name = member["name"].AsString;
                            if (name.ToLowerInvariant() == key)
                            {
                                String auth = "";
                                if (cnxStr.Split('@').Length == 2)
                                {
                                    auth = cnxStr.Split('@')[0].Replace("mongodb://", "") + "@";
                                }

                                this.Connexions[key] = new MongoClient(String.Format("mongodb://{0}{1}?wtimeout=5000&journal=false&connect=direct", auth, name));
                                break;
                            }
                        }

                        if (this.Connexions.ContainsKey(key))
                        {
                            break;
                        }
                    }
                }

                if (!this.Connexions.ContainsKey(key))
                {
                    Logger.Warn("No connection known for monitoring instance with key {0}", key);
                }
                return this.Connexions[key].GetDatabase(database);
            }
        }

        private List<String> MonitoredNodeNames
        {
            get
            {
                List<String> monitored = new List<string>();
                foreach (String cnx in cfg.MonitoredConnectionStrings)
                {
                    var tmpClient = new MongoClient(cnx);
                    IMongoDatabase tmp = tmpClient.GetDatabase("admin");

                    var isMaster = tmp.RunCommand(new BsonDocumentCommand<BsonDocument>(new BsonDocument("isMaster", 1)));
                    if (!isMaster.Contains("hosts"))
                    {
                        // Not a replica set. Just a single node.
                        monitored.Add(tmpClient.Settings.Server.Host + ":" + tmpClient.Settings.Server.Port);
                    }
                    else
                    {
                        // Replica set.                    
                        var rsStatus = tmp.RunCommand(new BsonDocumentCommand<BsonDocument>(new BsonDocument("replSetGetStatus", 1))); // must run against admin
                        foreach (var member in rsStatus["members"].AsBsonArray)
                        {
                            monitored.Add(member["name"].AsString);
                        }
                    }
                }
                return monitored;
            }
        }

        private String DiscoverHosts()
        {
            String res = "{ \"data\": [";
            res += string.Join(",", MonitoredNodeNames.Select(c => " { \"{#NODEKEY}\":\"" + c.Replace(":", "_") + "\"}"));
            res += "]}";

            return res;
        }

        private async Task<String> DiscoverDatabases()
        {
            String res = "{ \"data\": [";
            List<String> dbs = new List<string>();


            foreach (String nodeName in MonitoredNodeNames) // Connection cnx in cfg.Connections)
            {
                var a = GetDatabase(nodeName);
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
}
