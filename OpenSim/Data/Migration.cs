/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using log4net;

namespace OpenSim.Data
{
    /// <summary>
    ///
    /// The Migration theory is based on the ruby on rails concept.
    /// Each database driver is going to be allowed to have files in
    /// Resources that specify the database migrations.  They will be
    /// of the form:
    ///
    ///    001_Users.sql
    ///    002_Users.sql
    ///    003_Users.sql
    ///    001_Prims.sql
    ///    002_Prims.sql
    ///    ...etc...
    ///
    /// When a database driver starts up, it specifies a resource that
    /// needs to be brought up to the current revision.  For instance:
    ///
    ///    Migration um = new Migration(DbConnection, Assembly, "Users");
    ///    um.Update();
    ///
    /// This works out which version Users is at, and applies all the
    /// revisions past it to it.  If there is no users table, all
    /// revisions are applied in order.  Consider each future
    /// migration to be an incremental roll forward of the tables in
    /// question.
    ///
    /// Assembly must be specifically passed in because otherwise you
    /// get the assembly that Migration.cs is part of, and what you
    /// really want is the assembly of your database class.
    ///
    /// </summary>
    public class Migration
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected string _type;
        protected DbConnection _conn;
        protected Assembly _assem;

        /// <summary>Have the parameterless constructor just so we can specify it as a generic parameter with the new() constraint.
        /// Currently this is only used in the tests. A Migration instance created this way must be then
        /// initialized with Initialize(). Regular creation should be through the parameterized constructors.
        /// </summary>
        public Migration()
        {
        }

        public Migration(DbConnection conn, Assembly assem, string subtype, string type)
        {
            Initialize(conn, assem, type, subtype);
        }

        public Migration(DbConnection conn, Assembly assem, string type)
        {
            Initialize(conn, assem, type, "");
        }

        /// <summary>Must be called after creating with the parameterless constructor.
        /// NOTE that the Migration class now doesn't access database in any way during initialization.
        /// Specifically, it won't check if the [migrations] table exists. Such checks are done later:
        /// automatically on Update(), or you can explicitly call InitMigrationsTable().
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="assem"></param>
        /// <param name="subtype"></param>
        /// <param name="type"></param>
        public void Initialize (DbConnection conn, Assembly assem, string type, string subtype)
        {
            _type  = type;
            _conn  = conn;
            _assem = assem;
        }

        public void InitMigrationsTable()
        {
            // NOTE: normally when the [migrations] table is created, the version record for 'migrations' is
            // added immediately. However, if for some reason the table is there but empty, we want to handle that as well.
            int ver = FindVersion(_conn, "migrations");
            if (ver <= 0)   // -1 = no table, 0 = no version record
            {
                if (ver < 0)
                    ExecuteScript("create table migrations(name varchar(100), version int)");
                InsertVersion("migrations", 1);
            }
        }

        /// <summary>Executes a script, possibly in a database-specific way.
        /// It can be redefined for a specific DBMS, if necessary. Specifically,
        /// to avoid problems with proc definitions in MySQL, we must use
        /// MySqlScript class instead of just DbCommand. We don't want to bring
        /// MySQL references here, so instead define a MySQLMigration class
        /// in OpenSim.Data.MySQL
        /// </summary>
        /// <param name="conn"></param>
        /// <param name="script">Array of strings, one-per-batch (often just one)</param>
        protected virtual void ExecuteScript(DbConnection conn, string[] script)
        {
            using (DbCommand cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = 0;
                foreach (string sql in script)
                {
                    m_log.Debug ("[Migration]: ExecuteScript*: sql=" + sql);
                    cmd.CommandText = sql;
                    try
                    {
                        cmd.ExecuteNonQuery();
                    }
                    catch(Exception e)
                    {
                        throw new Exception(e.Message + " in SQL: " + sql);
                    }
                }
            }
        }

        protected void ExecuteScript(DbConnection conn, string sql)
        {
            ExecuteScript(conn, new string[]{sql});
        }

        protected void ExecuteScript(string sql)
        {
            ExecuteScript(_conn, sql);
        }

        protected void ExecuteScript(string[] script)
        {
            ExecuteScript(_conn, script);
        }

        public void Update()
        {
            InitMigrationsTable();

            int version = FindVersion(_conn, _type);

            SortedList<int, string[]> migrations = GetMigrationsAfter(version);
            if (migrations.Count < 1) {
                m_log.DebugFormat("[MIGRATIONS]: {0} data tables already up to date at revision {1}", _type, version);
                return;
            }

            // to prevent people from killing long migrations.
            m_log.InfoFormat("[MIGRATIONS]: Upgrading {0} to latest revision {1}.", _type, migrations.Keys[migrations.Count - 1]);
            m_log.Info("[MIGRATIONS]: NOTE - this may take a while, don't interrupt this process!");

            foreach (KeyValuePair<int, string[]> kvp in migrations)
            {
                int newversion = kvp.Key;
                // we need to up the command timeout to infinite as we might be doing long migrations.

                /* [AlexRa 01-May-10]: We can't always just run any SQL in a single batch (= ExecuteNonQuery()). Things like
                 * stored proc definitions might have to be sent to the server each in a separate batch.
                 * This is certainly so for MS SQL; not sure how the MySQL connector sorts out the mess
                 * with 'delimiter @@'/'delimiter ;' around procs.  So each "script" this code executes now is not
                 * a single string, but an array of strings, executed separately.
                */
                try
                {
                    ExecuteScript(kvp.Value);
                }
                catch (Exception e)
                {
                    m_log.DebugFormat("[MIGRATIONS]: Cmd was {0}", e.Message.Replace("\n", " "));
                    m_log.Debug("[MIGRATIONS]: An error has occurred in the migration.  If you're running OpenSim for the first time then you can probably safely ignore this, since certain migration commands attempt to fetch data out of old tables.  However, if you're using an existing database and you see database related errors while running OpenSim then you will need to fix these problems manually. Continuing.");
                    ExecuteScript("ROLLBACK;");
                }

                if (version == 0)
                {
                    InsertVersion(_type, newversion);
                }
                else
                {
                    UpdateVersion(_type, newversion);
                }
                version = newversion;
            }
        }

        public int Version
        {
            get { return FindVersion(_conn, _type); }
            set {
                if (Version < 1)
                {
                    InsertVersion(_type, value);
                }
                else
                {
                    UpdateVersion(_type, value);
                }
            }
        }

        // get current version of given table
        protected virtual int FindVersion(DbConnection conn, string type)
        {
            int version = 0;
            using (DbCommand cmd = conn.CreateCommand())
            {
                try
                {
                    cmd.CommandText = "select version from migrations where name='" + type + "' order by version desc";
                    using (DbDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            version = Convert.ToInt32(reader["version"]);
                        }
                        reader.Close();
                    }
                }
                catch
                {
                    // Something went wrong (probably no table), so we're at version -1
                    version = -1;
                }
            }
            m_log.Debug ("[Migrations]: FindVersion*: type=" + type + " version=" + version);
            return version;
        }

        private void InsertVersion(string type, int version)
        {
            m_log.InfoFormat("[MIGRATIONS]: Creating {0} at version {1}", type, version);
            ExecuteScript("insert into migrations(name, version) values('" + type + "', " + version + ")");
        }

        private void UpdateVersion(string type, int version)
        {
            m_log.InfoFormat("[MIGRATIONS]: Updating {0} to version {1}", type, version);
            ExecuteScript("update migrations set version=" + version + " where name='" + type + "'");
        }

        private delegate void FlushProc();

        // get list of migrations for the current _type that are after the given version
        //  output:
        //   returns list of sql statements sorted by version number:
        //    index = version, greater than supplied 'after'
        //    value = array of SQL statements to upgrade to the given version
        private SortedList<int, string[]> GetMigrationsAfter(int after)
        {
            SortedList<int, string[]> migrations = new SortedList<int, string[]> ();

            System.Text.StringBuilder sb = new System.Text.StringBuilder(4096);
            int nVersion = -1;

            List<string> script = new List<string>();

            FlushProc flush = delegate()
            {
                if (sb.Length > 0) {
                    script.Add(sb.ToString());
                    sb.Length = 0;
                }

                if ((nVersion > 0) && (nVersion > after) && (script.Count > 0) && !migrations.ContainsKey(nVersion)) {
                    migrations[nVersion] = script.ToArray();
                }
                script.Clear();
            };

            string sFile = _type + ".migrations";
            Stream resource = _assem.GetManifestResourceStream(sFile);
            if (resource != null) {
                using (StreamReader resourceReader = new StreamReader(resource))
                {
                    int nLineNo = 0;
                    for (string sLine; (sLine = resourceReader.ReadLine ()) != null;) {
                        nLineNo++;

                        string tLine = sLine.Trim ();

                        // ignore a comment or empty line
                        if (String.IsNullOrEmpty(tLine) || tLine.StartsWith("#"))
                            continue;

                        // ":GO" - marks end of SQL statement
                        if (tLine.Equals(":GO", StringComparison.InvariantCultureIgnoreCase))
                        {
                            if (sb.Length > 0) {
                                if (nVersion > after) script.Add (sb.ToString ());
                                sb.Length = 0;
                            }
                            continue;
                        }

                        // ":VERSION nnn" : ends previous version statement block if any
                        //                  and starts this verion's statement block
                        if (tLine.StartsWith(":VERSION ", StringComparison.InvariantCultureIgnoreCase))
                        {
                            // flush out previous version statement block if any
                            flush();

                            // Comment is allowed in version sections, ignored
                            int n = tLine.IndexOf('#');
                            if (n >= 0)
                                tLine = tLine.Substring(0, n);

                            // get new block version
                            if (!int.TryParse(tLine.Substring(9).Trim(), out nVersion))
                            {
                                m_log.ErrorFormat("[MIGRATIONS]: invalid version marker at {0}: line {1}. Migration failed!", sFile, nLineNo);
                                break;
                            }
                            continue;
                        }

                        // must be a SQL statement so save it
                        sb.AppendLine(sLine);
                    }

                    // end of file, flush final verion statement block
                    flush();
                }
            }

            return migrations;
        }
    }
}
