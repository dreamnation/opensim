
// Reads prebuild.xml file from current directory
// Writes corresponding makefile to stdout
// Requires make symbol MCS for the compiler
// - includes default: to build all output files
//              clean: to delete all output files

// mcs -debug -out:prebuildtomake.exe prebuildtomake.cs
// mono --debug prebuildtomake.exe | make -j8 -f - MCS=mcs [clean]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

public class PrebuildToMake {

    // one per .dll/.exe being created
    private class Project {
        public string name;
        public string output;
        public string outype;
        public LinkedList<string> refpaths = new LinkedList<string> ();
        public LinkedList<string> refddlls = new LinkedList<string> ();
        public LinkedList<string> sources  = new LinkedList<string> ();
        public LinkedList<string> resrces  = new LinkedList<string> ();
    }

    public static void Main ()
    {
        // read prebuild.xml and splice in any prebuild.xml's for modules
        string xmltext = File.ReadAllText ("prebuild.xml");
        for (int i; (i = xmltext.IndexOf ("<?include ")) >= 0;) {
            int f = xmltext.IndexOf ("file=\"", i) + 6;
            int g = xmltext.IndexOf ("\"", f);
            int j = xmltext.IndexOf ("?>", g) + 2;
            ProcessStartInfo si = new ProcessStartInfo ();
            si.WindowStyle = ProcessWindowStyle.Hidden;
            si.FileName = "/bin/bash";
            si.Arguments = "-c 'cat " + xmltext.Substring (f, g - f) + "'";
            si.RedirectStandardOutput = true;
            si.UseShellExecute = false;
            Process proc = new Process ();
            proc.StartInfo = si;
            proc.Start ();
            string subs = proc.StandardOutput.ReadToEnd ();
            proc.WaitForExit ();
            xmltext = xmltext.Substring (0, i) + subs + xmltext.Substring (j);
        }

        // parse the resulting XML document
        XmlDocument doc = new XmlDocument ();
        doc.LoadXml (xmltext);
        //dumpxml (doc, "##  ");

        XmlNode solution = doc["Prebuild"]["Solution"];
        string activeConfigName = getStringAttr (solution.Attributes["activeConfig"], "Debug");

        XmlNode activeConfig = null;
        foreach (XmlNode xn in solution.ChildNodes) {
            if ((xn.Name == "Configuration") && (xn.Attributes["name"].InnerText == activeConfigName)) {
                activeConfig = xn;
            }
        }

        string outputPath = "";
        string command = "$(MCS)";

        XmlNode options = activeConfig["Options"];
        if (options != null) {
            if (getBoolAttr (options["CheckUnderflowOverflow"], false)) {
                command += " -checked";
            }

            string compilerDefines = getStringAttr (options["CompilerDefines"], "");
            if (compilerDefines != "") {
                command += " -d:'";
                string sep = "";
                string[] parts = compilerDefines.Split (',');
                foreach (string part in parts) {
                    command += sep + part;
                    sep = ";";
                }
                command += "'";
            }

            if (getBoolAttr (options["DebugInformation"], false)) {
                command += " -debug";
            }

            if (getBoolAttr (options["NoStdLib"], false)) {
                command += " -nostdlib";
            }

            if (getBoolAttr (options["OptimizeCode"], false)) {
                command += " -optimize";
            }

            if (getBoolAttr (options["AllowUnsafe"], false)) {
                command += " -unsafe";
            }

            string warnlevel = getStringAttr (options["WarningLevel"], "4");
            if (warnlevel != "4") {
                command += " -warn:" + warnlevel;
            }

            outputPath = getStringAttr (options["OutputPath"], outputPath);
        }

        SortedDictionary<string,Project> projects = new SortedDictionary<string,Project> ();
        foreach (XmlNode xnproj in solution.ChildNodes) {
            if (xnproj.Name == "Project") {
                Project project = new Project ();

                string sourcePath = xnproj.Attributes["path"].InnerText;

                // get name of exe/dll being created
                project.name   = xnproj.Attributes["name"].InnerText;
                project.output = trimPath (outputPath + "/" + project.name);
                project.outype = getStringAttr (xnproj.Attributes["type"], "Exe");
                if (project.outype == "Exe")     project.output += ".exe";
                if (project.outype == "Library") project.output += ".dll";

                projects[project.name] = project;

                try {

                    // get list of directories to look for referenced dlls in
                    foreach (XmlNode xnref in xnproj.ChildNodes) {
                        if (xnref.Name == "ReferencePath") {
                            string rp = sourcePath + "/" + xnref.InnerText.Trim ();
                            rp = rp.Replace ("//", "/");
                            if (rp.EndsWith ("/")) rp = rp.Substring (0, rp.Length - 1);
                            project.refpaths.AddLast (rp);
                        }
                    }

                    // get list of referenced dlls
                    foreach (XmlNode xnref in xnproj.ChildNodes) {
                        if (xnref.Name == "Reference") {
                            string name = xnref.Attributes["name"].InnerText;
                            string path = getStringAttr (xnref.Attributes["path"], "");
                            if (path != "") name = trimPath (sourcePath + "/" + path + "/" + name);
                            project.refddlls.AddLast (name);
                        }
                    }

                    // get list of source files
                    foreach (XmlNode xnfil in xnproj.ChildNodes) {
                        if (xnfil.Name == "Files") {
                            foreach (XmlNode xnmat in xnfil.ChildNodes) {
                                if (xnmat.Name == "Match") {

                                    // get directory to look for the source file in
                                    string sourceSubPath = getStringAttr (xnmat.Attributes["path"], "");
                                    if (sourceSubPath == "") sourceSubPath = sourcePath;
                                    else sourceSubPath = sourcePath + "/" + sourceSubPath;

                                    // get list of names to exclude from the wildcard
                                    LinkedList<Regex> excludes = new LinkedList<Regex> ();
                                    foreach (XmlNode xnexcl in xnmat) {
                                        if (xnexcl.Name == "Exclude") {
                                            XmlNode xnpat = xnexcl.Attributes["pattern"];
                                            if (xnpat != null) {
                                                excludes.AddLast (new Regex (xnpat.InnerText));
                                            }
                                        }
                                    }

                                    // add files matching the wildcard pattern that aren't in the exclude list
                                    LinkedList<string> list = project.sources;
                                    if (getStringAttr (xnmat.Attributes["buildAction"], "") == "EmbeddedResource") {
                                        list = project.resrces;
                                    }
                                    addSourceFiles (list, sourceSubPath,
                                            xnmat.Attributes["pattern"].InnerText,
                                            getBoolAttr (xnmat.Attributes["recurse"], false),
                                            excludes);
                                }
                            }
                        }
                    }
                } catch (Exception e) {
                    Console.Error.WriteLine ("--------");
                    Console.Error.WriteLine ("exception processing project " + project.output);
                    Console.Error.WriteLine (e.ToString ());
                    Console.Error.WriteLine ("--------");
                }
            }
        }

        // now that we know all possible dll's and exe's we create,
        // resolve all referenced dll's and exe's to their path names
        foreach (Project project in projects.Values) {
            LinkedList<string> cooked = new LinkedList<string> ();
            foreach (string rawdll in project.refddlls) {
                if (rawdll.Contains ("/")) {
                    string r = rawdll;
                    if (!rawdll.EndsWith (".dll")) r += ".dll";
                    cooked.AddLast (r);
                    goto done;
                }
                foreach (Project p in projects.Values) {
                    if (p.name == rawdll) {
                        cooked.AddLast (p.output);
                        goto done;
                    }
                }
                cooked.AddLast (findRefdDll (project.refpaths, rawdll));
            done:;
            }
            project.refddlls = cooked;
        }

        // default target is to build all files
        writeOut ("\ndefault:");
        foreach (Project project in projects.Values) {
            writeOut (" \\\n    " + project.output);
        }
        writeOut ("\n\n");

        // clean is to delete everything we can possibly create
        writeOut ("clean:");
        foreach (Project project in projects.Values) {
            writeOut ("\n\trm -f " + project.output);
            writeOut ("\n\trm -f " + project.output + ".mdb");
            writeOut ("\n\trm -f " + project.output);
        }
        writeOut ("\n\n");

        // output makefile, one target for each project
        foreach (Project project in projects.Values) {

            // output normal target and dependencies
            writeOut (project.output + ":");
            foreach (string source in project.sources) {
                writeOut (" \\\n    " + source);
            }
            foreach (string resrce in project.resrces) {
                writeOut (" \\\n    " + resrce);
            }
            foreach (string refddll in project.refddlls) {
                if (refddll.Contains ("/")) writeOut (" \\\n    " + refddll);
            }

            // output compilation command
            writeOut ("\n\t" + command + " \\\n\t    -out:" + project.output);
            if (project.outype == "Exe")     writeOut (" -target:exe");
            if (project.outype == "Library") writeOut (" -target:library");
            foreach (string source in project.sources) {
                writeOut (" \\\n\t    " + source);
            }
            foreach (string resrce in project.resrces) {
                int i = resrce.LastIndexOf ("/");
                string id = resrce.Substring (++ i);
                writeOut (" \\\n\t    -resource:" + resrce + "," + id);
            }
            foreach (string refddll in project.refddlls) {
                writeOut (" \\\n\t    -r:" + refddll);
            }
            writeOut ("\n\n");
        }
    }

    // get boolean attribute from xml node
    private static bool getBoolAttr (XmlNode attr, bool def)
    {
        if (attr == null) return def;
        String str = attr.InnerText.Trim ();
        if (str == "") return def;
        if (str == "true") return true;
        if (str == "false") return false;
        throw new ApplicationException ("bad boolean " + attr.Name + "=" + str);
    }

    // get string attribute from xml node
    private static string getStringAttr (XmlNode attr, string def)
    {
        if (attr == null) return def;
        return attr.InnerText;
    }

    // look for a referenced dll
    //  input:
    //   refpaths = list of directories to look in
    //   refddll = dll to look for (without trailing .dll)
    //  output:
    //   returns dll full path (including trailing .dll) or original refddll with .dll if not found
    private static string findRefdDll (LinkedList<string> refpaths, string refddll)
    {
        if (!refddll.EndsWith (".dll")) refddll += ".dll";
        foreach (string refpath in refpaths) {
            string dllpath = trimPath (refpath + "/" + refddll);
            if (File.Exists (dllpath)) return dllpath;
        }
        return refddll;
    }

    // add all matching source files to the given list
    //  input:
    //   sourcePath = directory to search in
    //   pattern = wildcard pattern to search for
    //   recurse = whether or not to recurse on directories
    //   excludes = list of files to exclude
    //  output:
    //   sources = matching entries appended
    private static void addSourceFiles (LinkedList<string> sources, string sourcePath, string pattern, bool recurse,
            LinkedList<Regex> excludes)
    {
        SearchOption option = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        string[] paths;
        try {
            paths = Directory.GetFiles (trimPath (sourcePath), pattern, option);
        } catch (DirectoryNotFoundException) {
            // get these for some of those Resources directories
            return;
        }
        foreach (string path in paths) {
            foreach (Regex exclude in excludes) {
                if (exclude.IsMatch (path)) goto skip;
            }
            sources.AddLast (path);
        skip:;
        }
    }

    // trim redundant /./ and /../ from a path name
    private static string trimPath (string path)
    {
        path = path.Replace ("/./", "/").Replace ("//", "/");
        for (int i; (i = path.IndexOf ("/../")) > 0;) {
            int j = path.LastIndexOf ("/", i - 1) + 1;
            path = path.Substring (0, j) + path.Substring (i + 4);
        }
        return path;
    }

    // write string to makefile
    private static void writeOut (string str)
    {
        Console.Write (str);
    }

    // dump xml node for debugging
    private static void dumpxml (XmlNode node, string indent)
    {
        if (node.Name == "#text") {
            Console.WriteLine (indent + node.Name + ":" + node.InnerText);
        } else {
            Console.WriteLine (indent + node.Name + ":");
            XmlAttributeCollection attributes = node.Attributes;
            if (attributes != null) {
                foreach (XmlAttribute attr in attributes) {
                    Console.WriteLine (indent + "  " + attr.Name + "=" + attr.InnerText);
                }
            }
            XmlNodeList children = node.ChildNodes;
            foreach (XmlNode child in children) {
                dumpxml (child, indent + "  ");
            }
        }
    }
}
