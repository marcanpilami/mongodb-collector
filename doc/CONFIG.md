# Collector configuration

Main structure:
```
{
    "RefreshPeriodSecond": 60,
    "RefreshConfigurationMinute": 10,

    "Connections": [
        {
            "ConnectionString": "mongodb://localhost:27017?wtimeout=5000&journal=false&replicaSet=rsname",
        }
    ],

    TargetConnection: {
        "ConnectionString": "mongodb://192.168.13.54:27017?wtimeout=5000&journal=false",
        "DatabaseName": "mydb",
    },

    "Items": [
        {
            "Path": "serverStatus/metrics/commands/aggregate/total",
            "JsonUniqueName": "queries_aggregate_count",
            "Mode": "diff",
            "InSummary": true
        },
        {
            "Path": "serverStatus/metrics/commands/count/total",
            "JsonUniqueName": "queries_count_count",
            "InSummary": true
        }
    ]
}
```

### General parameters

RefreshPeriodSecond: for later use. Should be left at 60 seconds for now.

RefreshConfigurationMinute: the service will re-read its configuration file (items section only) every RefreshConfigurationMinute minutes. So there is no need to restart the service after an item configuration change.

### Connections

This section contains all the MongoDB clusters or single instances that should be monitored. If monitoring a replica set, a single connection is required, and 
the program will automatically connect to all nodes. Do not forget the "replicaSet" parameter in your connection string in that case.

For SECONDARY nodes, the connection will be read only.

For PRIMARY nodes, three collections will be added containing the monitoring data for the whole replica set.

The different parameters that can be set:
* ConnectionString - compulsory - a standard MongoDB connection string

An optional item, TargetConnection can be used to specify where to store the data. By default, 
the system will store the data inside the monitored databases themselves. This option allows to centralize
the data and therefore the publication of the results. It has the same syntax as the other connections, with an additional option "DatabaseName"
designating the database in which to store the data. It is not set by default.

### Items

This section contains all the items that should be monitored. Please note that the package comes with a default item configuration that should be a good start for most environments, and therefore few modifications are expected in this section.

Each item may have different parameters:
* Path - compulsory - the path inside the different MongoDB documents. See below.
* JsonUniqueName - compulsory - must be unique and a valid javascript variable name - the name this item will be stored as. It's "key", which is used inside queries.
* Mode - optional, default is diff
  * diff: the value stored is the delta between the current measure and the previous measure (one measure interval ago).
  * value: the value is stored as-is
* InSummary - optional, default is false - set to true for this item to be added to the summary document
* Multiplier - optional, default is 1 - the value will be multiplied by this value beofre being stored. This helps convert value to sensible units.

#### Item path

This service can fetch values from three different documents inside a MongoDB instance:
* top 
* serverStatus
* dbStats

These documents are complex and we only need to extract a few values from them. These items are deignated by
* a root, that is one of the documents listed above
* a path corresponding to the navigation insde the JSON document

For example, the count of the "count commands" will be serverStatus/metrics/commands/count/total.


#### Hard-coded items

Items that are hard coded, only present in the "latest status" document and not historized:
* MongoDB version
* uptime
* host name
* host FQDN (may be null as it depends on reverse DNS resolution by the database)
