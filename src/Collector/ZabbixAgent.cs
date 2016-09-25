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

namespace Collector
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
            if (this.Connexions.ContainsKey(key))
            {
                return this.Connexions[key].GetDatabase(database);
            }
            else
            {
                string host;
                string port;
                if (key.Contains(":"))
                {
                    host = key.Split(':')[0];
                    port = key.Split(':')[1];
                }
                else
                {
                    host = key.Split('_')[0];
                    port = key.Split('_')[1];
                }
                var cnx = this.cfg.MonitoredConnectionStrings.Single(c => c.Contains(host) && c.Contains(":" + port)); // throws exception if missing

                this.Connexions[key] = new MongoClient(cnx + "&connect=direct");
                return this.Connexions[key].GetDatabase(database);
            }
        }

        private String DiscoverHosts()
        {
            // For now, hosts come from configuration.
            String res = "{ \"data\": [";
            //TODO: res += string.Join(",", cfg.MonitoredConnectionStrings.Select(c => " { \"{#NODEKEY}\":\"" + c.NodeName.Replace(":", "_") + "\"}"));
            res += "]}";

            return res;
        }

        private async Task<String> DiscoverDatabases()
        {
            String res = "{ \"data\": [";
            List<String> dbs = new List<string>();
            //TODO: mlk
            /*foreach (Connection cnx in cfg.Connections)
            {
                var a = GetDatabase(cnx.NodeName);
                var list = await a.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("listDatabases", 1)));
                foreach (BsonValue db in list["databases"].AsBsonArray)
                {
                    dbs.Add("{ \"{#NODEKEY}\":\"" + cnx.NodeName + "\", \"{#DBNAME}\":\"" + db["name"] + "\"}");
                }
            }

            res += string.Join(",", dbs);
            res += "]}";*/

            return res;
        }
    }
}
