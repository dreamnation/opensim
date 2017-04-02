#!/bin/bash
set -e
cd `dirname $0`
if [ prebuildtomake.exe -ot prebuildtomake.cs ]
then
    mcs -debug -out:prebuildtomake.exe prebuildtomake.cs
fi
mono --debug prebuildtomake.exe > makefile
make -f makefile MCS=mcs "$@"

function filternames
{
    natives=' openjpeg-dotnet.dll BulletSim.dll ode.dll openjpeg-dotnet-x86_64.dll Npgsql.dll sqlite3.dll '
    while read name
    do
        nameonly=${name##*/}
        if [ "${natives/ $nameonly /}" != "$natives" ]
        then
            continue
        fi
        if [ $name.so -ot $name ]
        then
            echo $name
        fi
    done
}

function deloldsos
{
    while read nameso
    do
        name=${nameso%.so}
        if [ ! -f $name ]
        then
            rm $nameso
            continue
        fi
        if [ $nameso -ot $name ]
        then
            rm $nameso
            continue
        fi
    done
}

find bin -name \*.dll | filternames | xargs -n 1 -P 8 -r -t mono --aot -O=all
find bin -name \*.exe | filternames | xargs -n 1 -P 8 -r -t mono --aot -O=all
find bin -name \*.dll.so | deloldsos
find bin -name \*.exe.so | deloldsos

