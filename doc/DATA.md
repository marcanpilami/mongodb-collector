# Result data

## Structure

The service creates two collections inside the target databases (if the databases are inside a 
replica set, all the data concerning the set is of course inserted through the master node).
* mongoperf_detail
* mongoperf_summary

The first collection contains the historical data. Its structure is very simple; there is one document 
per item per hour, with minutes being fields of the document (this is done to avoid creating millions 
of documents, which is not a good idea).
Fields are:
* node - the name of the node
* key - the key of the item as specified in parameters
* when - a date/time object truncated to the hour
* values - a subdocument containing fields named from 0 to 59, initialized at -1

The second collection is a recap designed to give all data needed by an external reporting system 
(Nagios, Zabbix, ...) in a single query. 
It only contains the latest value of selected (see configuration) items. There is one document per 
MongoDB node.
* _measure - the date/time of the latest measure
* node - the name of the MongoDB node
* ... and all the items specified in configuration, named with their keys.

## Exploitation

The data can be directly queried inside MongoDB.

It can also be obtained through a web service call. The summary, for example can be obtained 
by a GET on api/NODENAME/summary

See the full WS documentation (TODO) for more details.