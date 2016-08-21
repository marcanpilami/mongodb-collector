# MongoDB metrics collector

This is a small utility with no prentions whatsoever trying to solve very simply the common question 
*how do I extract the many monitoring items of MongoDB toward my monitoring or capacity planning system*?

It also provides an optional first level analysis web console for environments without a dedicated 
monitoring or capacity planning system.

It can also be used a specialized Zabbix agent for MongoDB (but is is in no way limited to being used with Zabbix only).

It is cut in two parts providing different services:
* a daemon/service, which 
    * collects the data and stores it inside a MongoDB database. All data is historized.
    * acts as a Zabbix agent, and therefore can be queried directly from Zabbix (including auto discovery).
* an optional web application (self hosted, can be put behind a reverse proxy) providing
    * a web dashboard giving the instant health of you DB as well as the ability to query the history
    * a set of very simple web services (JSON or XML) to enable you to retrieve the data from another 
      system (Nagios, RRDB, ...)

It is extremely easy to use:    
* Runs on Windows and Linux.
* Packaged as RPM (Linux) and Chocolatey (Windows).
* Its only dependency is dotnet (if you wish, you can even build yourself the project without this).
* Compatible both with single instances and replica sets.
* Can be deployed either on the DB server itself, or on another machine (as it uses standard MongoDB connections).
* Only collect the metrics you need - it is fully customizable, and provided with a default configuratin which should suit most users.

# Installation

See [the installation documentation](./doc/INSTALL.md). Note that a single installation 
can monitor multiple MongoDB instances, so a single centralized instance may be enough for all your servers.

After the install, if you want to use the Zabbix MongoDB agent, follow [this procedure](./doc/ZABBIX_INSTALL.md).

# Configuration

See [this document](./doc/CONFIG.md).
