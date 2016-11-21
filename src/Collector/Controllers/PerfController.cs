using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using monitoringexe;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace agent.web
{
    public class PerfController : Controller
    {
        private readonly IOptions<Configuration> config;
        private readonly IMongoDatabase database;
        private readonly MongoClient client;

        private readonly IMongoCollection<BsonDocument> mongo_detail;
        private readonly IMongoCollection<BsonDocument> mongo_summary;
        private readonly IMongoCollection<BsonDocument> mongo_trace;

        public PerfController(IOptions<Configuration> cfg)
        {
            this.config = cfg;
            this.client = new MongoClient(cfg.Value.ResultsStorageConnection.ConnectionString);
            this.database = client.GetDatabase(cfg.Value.ResultsStorageConnection.DatabaseName);

            this.mongo_detail = database.GetCollection<BsonDocument>(cfg.Value.DetailCollectionName, new MongoCollectionSettings { ReadPreference = ReadPreference.SecondaryPreferred });
            this.mongo_summary = database.GetCollection<BsonDocument>(cfg.Value.SummaryCollectionName, new MongoCollectionSettings { ReadPreference = ReadPreference.SecondaryPreferred });
            this.mongo_trace = database.GetCollection<BsonDocument>(config.Value.ProfilerCollectionName, new MongoCollectionSettings { ReadPreference = ReadPreference.SecondaryPreferred });
        }

        /// <summary>
        /// The summary web page. It is full of JS and calls the other services on this controller.
        /// </summary>    
        [Route("")]
        public IActionResult DashBoard()
        {
            return View();
        }

        [Route("api/{nodename}/{key}/{from}/{to}")]
        [ResponseCache(NoStore = true)]
        public async Task<IEnumerable<KeyValuePair<DateTime, long>>> GetMongoPerfData(String nodename, DateTime from, DateTime to, String key)
        {
            return (await mongo_detail.Find(d => d["node"] == nodename && d["key"] == key && d["when"] <= to && d["when"] > from.AddHours(-1)).Sort("{when: 1}").ToListAsync()).SelectMany(l => l["values"].AsBsonDocument.ToDictionary(v => l["when"].ToUniversalTime().AddMinutes(int.Parse(v.Name)), v => (long)v.Value)).Where(t => t.Value != -1);
        }

        [Route("api/{nodename}/summary")]
        [ResponseCache(NoStore = true)]
        public async Task<BsonDocument> GetMongoPerfDataSummary(String nodename)
        {
            return await mongo_summary.Find(d => d["node"] == nodename).SingleAsync();
        }

        [Route("api/keys")]
        [ResponseCache(Duration = 300)]
        public object GetMongoPerfQueryParameters()
        {
            return mongo_detail.AsQueryable().Select(d => new { Node = d["node"], Key = d["key"] }).Distinct().ToList().OrderBy(o => o.Node).ThenBy(o => o.Key).GroupBy(o => o.Node).ToDictionary(o => o.Key, o => o.Select(p => p.Key));
        }

        [Route("api/slow/{count}")]
        [Route("api/slow")]
        [ResponseCache(NoStore = true)]
        public async Task<IEnumerable<BsonDocument>> GetMongoLongQueries(int count = 100)
        {
            if (!(await database.ListCollections(new ListCollectionsOptions { Filter = new BsonDocument("name", "system.profile") }).ToListAsync()).Any())
            {
                return null;
            }
            return await this.mongo_trace.Find(s => true).Sort(new SortDefinitionBuilder<BsonDocument>().Descending("$natural")).Limit(count).ToListAsync();
        }
    }
}
