#!/bin/bash

cd $(dirname $0)
resDir=$(pwd)
cd $(dirname $0)/../..
repoRoot=$(pwd)

tmpDir=/tmp/build-agent
publishRoot=$tmpDir/publish
rpmRoot=$tmpDir/rpm

# Get version
version=$(grep "^  \"version" ${repoRoot}/src/Collector/project.json | cut -d':' -f2 | sed "s|.*\"\(.*\)-.*|\1|g")

# Main RPM macros
echo "%_topdir ${rpmRoot}
%_tmppath %{_topdir}/tmp
%_version ${version}
%debug_package %{nil}
" >~/.rpmmacros

# Distrib specifics (no dotnet repo package in centos)
grep -i centos /etc/os-release > /dev/null
if [[ $? -eq 0 ]]
then
    # CentOS
    export dotnetPath=/usr/local/bin/dotnet 
else
    # RHEL
    export dotnetPath=/opt/rh/rh-dotnetcore10/root/usr/bin/dotnet    
fi

# BUILD
#scl enable rh-dotnetcore10 bash
rm -rf $tmpDir
mkdir -p ${publishRoot}/{Collector}
dotnet restore src/Collector
dotnet publish src/Collector --output ${publishRoot}/Collector --configuration Release --runtime "centos.7-x64"

# Create TGZ for RPMA
tarRoot=${rpmRoot}/mongodb-agent-${version}
mkdir -p ${rpmRoot}/{RPMS,SRPMS,BUILD,SOURCES,SPECS,tmp}
mkdir -p ${tarRoot}/opt/mongodb-agent
mkdir -p ${tarRoot}/var/log/mongodb-agent
mkdir -p ${tarRoot}/etc/mongodb-agent
mkdir -p ${tarRoot}/usr/lib/systemd/system/

cat ${resDir}/mongodb-agent.service | envsubst > ${tarRoot}/usr/lib/systemd/system/mongodb-agent.service

cp ${publishRoot}/Collector/settings.json    ${tarRoot}/etc/mongodb-agent/mongodb-agent.conf
cat ${publishRoot}/Collector/nlog.config | sed "s|./logs/collector.log|/var/log/mongodb-agent/collector.log|g" > ${tarRoot}/etc/mongodb-agent/nlog.config

cp -r ${publishRoot}/Collector/* ${tarRoot}/opt/mongodb-agent/
rm -f ${tarRoot}/opt/mongodb-agent/settings.json ${tarRoot}/opt/mongodb-agent/nlog.config

tar -zcf ${rpmRoot}/mongodb-agent-${version}.tar.gz -C $(dirname ${tarRoot}) ./$(basename ${tarRoot})
if [[ $? -ne 0 ]]
then
	exit 1
fi
mv ${rpmRoot}/mongodb-agent-${version}.tar.gz ${rpmRoot}/SOURCES/
rm -rf ${tarRoot}


# Create RPM
cd ${rpmRoot}
cp ${resDir}/mongodb-agent.spec ${rpmRoot}/SPECS/
rpmbuild -ba SPECS/mongodb-agent.spec
cd /tmp

# Cleanup
echo "RPM placed inside your home directory:"
find ${rpmRoot}/RPMS -name "*.rpm" -exec basename {} \; -exec mv {} ~ \;
rm -rf ${tmpDir}
