#!/bin/bash -v

cd `dirname $0`
cd bin

COMMITHASH=`git log -1 | grep ^commit | sed 's/commit//g'`
COMMITDATE=`git log -1 --date=iso | grep ^Date: | sed 's/Date://g'`
COMMITDIRT=`git status | grep modified:`
if [ "$COMMITDIRT" ]
then
    COMMITDIRT="(dirty)"
fi
echo '' $COMMITHASH $COMMITDIRT $COMMITDATE > .version

