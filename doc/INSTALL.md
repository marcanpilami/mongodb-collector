# Installation

## 0. Configuration on the database instances

If authentication is disabled, there is nothing to do on the monitored instances.

Otherwise, there are two instance families:
* the monitored instances. You need an account on the admin database with the "clusterMonitor" role on the "admin" database. This will give it enough permissions to monotor all databases inside the instance.
* the database which will store the collected data (if collection is enabled). You need a user with "readWrite" role on that database (named by default datacollector - but it can be any database).

It is perfectly possible to use the same user for both. The following CLI script will create such a user (this is an example - you are free to create it or reuse an existing user as you like):

```
use admin
db.createUser(
  {
    user: "monitoring",
    pwd: "sdmlk09808kjdfllkj-",
    roles: [ { role: "clusterMonitor", db: "admin" },  { role: "readWrite", db: "datacollector" } ]
  }
)
```

## 1. Linux

On Linux, the two parts of the program (the collector itself, as well as the self-hosted web application) 
are two systemd services with:
* configuration inside /etc/mongodb-collector/
* logs inside /var/log/mongodb-collector/. Logs are auto rotated every day and purged after 10 days.
* binaries inside /opt/mongodb-collector/ (can be read only)

The services run with a user account named 'collector' (it's a system account: uid is below 1000) created during installation.

Installation is done with yum.


### 1.1. RHEL 7.x and CentOS 7.x


Everything must be run as root. Note that you do not need anymore to enable the official .NET repository - the package contains the minimal .NET runtime.

Download the RPM and run it (change the version to the one you have downloaded).
```
version="1.0.0"
wget https://github.com/marcanpilami/mongodb-collector/releases/download/${version}/mongodb-collector-${version}-1.el7.x86_64.rpm
yum install mongodb-collector-${verion}-1.el7.x86_64.rpm
```

Go to /etc/mongodb-collector and edit the connection strings (and if needed login and password) inside the two .conf files, 
the publisher's URL and any other value you wish (see configuration page for the meaning of configuration items).

Then you can enable the collector and the publisher:
```
# Start collector/agent
systemctl enable mongodb-collector-collector.service
systemctl start mongodb-collector-collector.service

# Start publish web services and web dashboard
systemctl enable mongodb-collector-publisher.service
systemctl start mongodb-collector-publisher.service

# Check
systemctl status mongodb-collector-publisher.service
systemctl status mongodb-collector-collector.service
```


### 1.2. Ubuntu 14.x+

TODO: the program is not yet packaged for this platform. You can however easily build it yourself.

## 2. Windows

TODO.

## 3. Test

Check if the page http://yourserver:5100 (default port) shows you a page with indicators 
(no blank spaces).