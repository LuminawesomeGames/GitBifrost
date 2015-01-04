// Copyright (c) 2014 Luminawesome Games, Ltd. All Rights Reserved.

using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Threading;
using System.Text;

#if __MonoCS__
using Mono.Unix.Native;
#endif // __MonoCS__

namespace GitBifrost
{
    delegate int CommandDelegate(string[] args);

    enum LogNoiseLevel
    {
        Normal = 0,
        Loud = 1,
        Debug = 2
    }

    class BifrostPoxy
    {
        public string FileRev;
        public string Version;
        public string SHA1;
        public long FileSize;

        public BifrostPoxy(string fileRev, string version, string sha1, long fileSize)
        {
            FileRev = fileRev;
            Version = version;
            SHA1 = sha1;
            FileSize = fileSize;
        }

    }

    class Program
    {
        const int BifrostVersion = 1;
        const string BifrostProxySignature = "~*@git-bifrost@*~";
        const string LocalStoreLocation = "./.git/bifrost/data";
        const string GitEmptySha = "0000000000000000000000000000000000000000";
        const int FirstFewBytes = 8000;
        const string TextSizeThresholdKey = "repo.text-size-threshold";
        const int DefaultTextSizeThreshold = 5 * Megabyte;
        const string BinSizeThresholdKey = "repo.bin-size-threshold";
        const int DefaultBinSizeThreshold = 100 * Kilobyte;
        const int Kilobyte = 1024;
        const int Megabyte = 1024 * 1024;
        const int Succeeded = 0;
        const int Failed = -1;

        static readonly char[] NullChar = new char[] { '\0' };

        static LogNoiseLevel NoiseLevel = LogNoiseLevel.Normal;

        static int Main(string[] args)
        {
            int result = Succeeded;

            Enum.TryParse(Environment.GetEnvironmentVariable("GITBIFROST_VERBOSITY"), true, out NoiseLevel);

            Dictionary<string, CommandDelegate> Commands = new Dictionary<string, CommandDelegate>(10);

            Commands["hook-pre-push"] = CmdHookPrePush;
            Commands["hook-pre-commit"] = CmdHookPreCommit;
            Commands["filter-clean"] = CmdFilterClean;
            Commands["filter-smudge"] = CmdFilterSmudge;
            Commands["verify"] = CmdVerify;
            Commands["help"] = CmdHelp;
            Commands["clone"] = CmdClone;
            Commands["init"] = CmdInit;

            string arg_command = args.Length > 0 ? args[0].ToLower() : "help";

            if (arg_command != null)
            {
                LogLine(LogNoiseLevel.Debug, "Bifrost: {0}", string.Join(" ", args));
                LogLine(LogNoiseLevel.Debug, "Bifrost: Current Dir: {0}", Directory.GetCurrentDirectory());
                // LogLine("PATH: {0}", Environment.GetEnvironmentVariable("PATH"));

                CommandDelegate command;
                if (!Commands.TryGetValue(arg_command, out command))
                {
                    command = CmdHelp;
                }
                result = command(args);
            }

            return result;
        }

        static int CmdHookPrePush(string[] args)
        {
            // This hook is called with the following parameters:
            //
            // $1 -- Name of the remote to which the push is being done
            // $2 -- URL to which the push is being done
            //
            // If pushing without using a named remote those arguments will be equal.
            //
            // Information about the commits which are being pushed is supplied as lines to
            // the standard input in the form:
            //
            //   <local ref> <local sha1> <remote ref> <remote sha1>

            string arg_remote_name = args[1];
            string arg_remote_url = args[2];

            char[] proxy_sig_buffer = new char[BifrostProxySignature.Length];

            //
            // Collect up all the file revisions we need to push to the stores
            //

            var proxy_revs = new List<BifrostPoxy>();

            using (StreamReader stdin = new StreamReader(Console.OpenStandardInput()))
            {
                LogLine(LogNoiseLevel.Debug, "Bifrost: Building list of file revivions to push to store.");

                string push_info = null;
                while ((push_info = stdin.ReadLine()) != null)
                {
                    LogLine(LogNoiseLevel.Debug, "Bifrost: push info ({0})", push_info);

                    string[] push_tokens = push_info.Split(' ');

                    string local_ref = push_tokens[0];
                    string local_sha = push_tokens[1];
//                    string remote_ref = push_tokens[2];
//                    string remote_sha = push_tokens[3];

                    if (local_sha != GitEmptySha)
                    {
                        // Get the individual revision ids between local and the remote commit id's
                        var rev_ids = new List<string>();
                        {
                            /* When performing a git-push, we need to determine what files are in the bifrost internal store and that have yet to be pushed to a primary store.
                             * This means that we need to comb through all files in all commits in local_ref that have yet to be pushed. 
                             * If you've been working for a very long time without a push, this can take a bit of time.
                             * The reason we don't simply push everything in the bifrost internal store is becasue we want to verify 
                             * the integrity of the local store (ie no files are missing/hashes checkout) before we can safely share everything with a new repository.
                             */

                            Process git_proc = StartGit(string.Format("rev-list {0} --not --remotes={1}", local_ref, arg_remote_name));

                            string rev_line = null;
                            while ((rev_line = git_proc.StandardOutput.ReadLine()) != null)
                            {
                                rev_ids.Add(rev_line);
                            }

                            if (git_proc.WaitForExitFail(true))
                            {
                                return git_proc.ExitCode;
                            }
                        }

                        LogLine(LogNoiseLevel.Debug, "Bifrost: Iterating file revisions");

                        string progress_msg = string.Format("Bifrost: Scanning tracked files in '{0}'", local_ref);
                       
                        int revision_index = 0;
                        foreach (string revision in rev_ids)
                        {
                            Log(LogNoiseLevel.Normal, GetProgressString(progress_msg, revision_index++, rev_ids.Count, "\r"));

                            string[] revision_files = GetFilesAndStatusInRevision(revision);
                                
                            LogLine(LogNoiseLevel.Debug, "Bifrost: Revision {0}, {1} file(s)", revision, revision_files.Length);

                            for (int i = 0; i < revision_files.Length; i += 2)
                            {
                                string status = revision_files[i];
                                string file = revision_files[i + 1];

                                if (status == "X")
                                {
                                    LogLine(LogNoiseLevel.Normal, "Bifrost: According to git something has gone wrong. Aborting push.");
                                    return Failed;
                                }

                                // Skip files that have been deleted
                                if (status != "D")
                                {
                                    string file_rev = string.Format("{0}:{1}", revision, file);
                                    BifrostPoxy proxy = GetProxyData(file_rev);

                                    if (proxy != null)
                                    {
                                        LogLine(LogNoiseLevel.Debug, "Bifrost: Will push '{0}'", file);
                                        proxy_revs.Add(proxy);
                                    }
                                }
                            }
                        }

                        LogLine(LogNoiseLevel.Normal, GetProgressString(progress_msg, revision_index, rev_ids.Count, ", done."));
                    }
                    else
                    {
                        // Empty
                        // Commit deleted?
                    }
                }
            }

            if (proxy_revs.Count > 0 && !Directory.Exists(LocalStoreLocation))
            {
                LogLine(LogNoiseLevel.Normal, "Bifrost: Local store directory is missing but should contain files.");
                foreach (var file_rev in proxy_revs)
                {
                    LogLine(LogNoiseLevel.Normal, file_rev.FileRev);
                }
                return Failed;
            }

            if (proxy_revs.Count == 0)
            {
                LogLine(LogNoiseLevel.Normal, "Bifrost: No files to push.");
                return Succeeded;
            }

            //
            // Update the stores with the files
            //

            LogLine(LogNoiseLevel.Normal, "Bifrost: Updating store(s) for remote '{0}' ({1})", arg_remote_name, arg_remote_url);

            var store_interfaces = GetStoreInterfaces();
            var store_infos = GetStores();

            LogLine(LogNoiseLevel.Debug, "Bifrost: Available stores: {0}", store_infos.Count);

            int primaries_updated = 0;

            foreach (var store_data in store_infos)
            {
                string remote_url_string;
                if (!store_data.TryGetValue("remote", out remote_url_string))
                {
                    LogLine(LogNoiseLevel.Debug, "Bifrost: Could not find remote url for store mapping '{0}'", store_data["name"]);
                    continue;
                }

                remote_url_string = Path.GetFullPath(remote_url_string);

                if (remote_url_string != arg_remote_url)
                {
                    LogLine(LogNoiseLevel.Debug, "Bifrost: Stores {0} & {1} are not the same", remote_url_string, arg_remote_url);
                    continue;
                }

                Uri store_uri = new Uri(store_data["url"]);

                IStoreInterface store_interface = store_interfaces[store_uri.Scheme];
                if (store_interface == null || !store_interface.OpenStore(store_uri, store_data))
                {
                    LogLine(LogNoiseLevel.Loud, "Couldn't open store '{1}' ({0})", store_uri, store_data["name"]);
                    continue;
                }

                int files_pushed = 0;
                int files_skipped = 0;
                int files_skipped_late = 0;

                string progress_msg = string.Format("Bifrost: Updating store '{0}'", store_uri.AbsoluteUri);

                int file_index = 0;
                foreach (var proxy_rev in proxy_revs)
                {
                    Log(LogNoiseLevel.Normal, GetProgressString(progress_msg, file_index, proxy_revs.Count, "\r"));
                    ++file_index;

                    // Build the mangled name
                    string filename = GetFilePathFromSHA(proxy_rev.SHA1);
                    string filepath = Path.Combine(LocalStoreLocation, filename);

                    SyncResult result = SyncResult.Failed;

                    if (File.Exists(filepath))
                    {
                        result = store_interface.PushFile(filepath, store_uri, filename);
                    }
                    else
                    {
                        LogLine(LogNoiseLevel.Normal, "Bifrost: Failed to find revision '{0}' in local store.", proxy_rev);
                    }

                    if (result == SyncResult.Failed)
                    {
                        store_interface.CloseStore();
                        LogLine(LogNoiseLevel.Normal, "Bifrost: Failed to push file {0} to {1}.", filepath, store_uri.LocalPath);
                        return Failed;
                    }
                    else if (result == SyncResult.Success)
                    {
                        ++files_pushed;
                    }
                    else if (result == SyncResult.Skipped)
                    {
                        ++files_skipped;
                    }
                    else if (result == SyncResult.SkippedLate)
                    {
                        ++files_skipped_late;
                    }
                }


                store_interface.CloseStore();

                LogLine(LogNoiseLevel.Normal, GetProgressString(progress_msg, file_index, proxy_revs.Count, ", done."));

                if (IsPrimaryStore(store_data))
                {
                    ++primaries_updated;
                }

                LogLine(LogNoiseLevel.Normal, "Bifrost: {0} Copied, {1} Skipped", files_pushed, files_skipped + files_skipped_late);
                LogLine(LogNoiseLevel.Loud, "Bifrost: {0} Copied, {1} Skipped, {2} Skipped late", files_pushed, files_skipped, files_skipped_late);
            }

            if (primaries_updated <= 0)
            {
                LogLine(LogNoiseLevel.Normal, "Bifrost: Failed to update a primary store for this remote.");
                return Failed;
            }


            return Succeeded;
        }

        static bool IsPrimaryStore(Dictionary<string, string> store_data)
        {
            string value;
            if (store_data.TryGetValue("primary", out value))
            {
                return bool.Parse(value);
            }

            return false;
        }

        // Verify that the staged files are reasonable to fit in a git repo filtered by bifrost
        static int CmdHookPreCommit(string[] args)
        {
            string[] staged_files = null;

            // Load get a list of the staged files
            {
                Process git_proc = StartGit("diff --name-only --cached -z");

                string proc_output = git_proc.StandardOutput.ReadToEnd();

                if (git_proc.WaitForExitFail(true))
                {
                    LogLine(LogNoiseLevel.Normal, "Git failed");
                    return git_proc.ExitCode;
                }

                staged_files = proc_output.Split(NullChar, StringSplitOptions.RemoveEmptyEntries);
            }

            byte[] scratch_buffer = new byte[64 * Kilobyte];

            bool succeeded = true;
            bool files_over_limit = false;
            bool files_need_restaging = false;

            int file_number = 0;

            int proxyid_numbytes = Encoding.UTF8.GetByteCount(BifrostProxySignature);

            string progress_msg = "Bifrost: Validating staged files";
            foreach (string file in staged_files)
            {
                bool bifrost_filtered = GetFilterAttribute(file) == "bifrost";

                Log(LogNoiseLevel.Normal, GetProgressString(progress_msg, file_number++, staged_files.Length, "\r"));

                using (Process git_proc = StartGit(string.Format("cat-file blob :\"{0}\"", file)))
                {
                    Stream proc_stream = git_proc.StandardOutput.BaseStream;
                    if (bifrost_filtered)
                    {
                        // If the file is already filtered by bifrost, make sure that the staged file is in fact
                        // a bifrost proxy file if it isn't the user needs to restage the file in order to allow the clean filter to run.
                        // This is likely to happen when you modify your git attributes to include a file in bifrost.

                        int bytes_read = proc_stream.Read(scratch_buffer, 0, proxyid_numbytes);

                        string proxyfile_tag = Encoding.UTF8.GetString(scratch_buffer, 0, bytes_read);

                        if (proxyfile_tag != BifrostProxySignature)
                        {
                            LogLine(LogNoiseLevel.Loud, "Bifrost: Needs restaging '{0}'.", file);
                            succeeded = false;
                            files_need_restaging = true;
                        }

                        LogLine(LogNoiseLevel.Debug, "Bifrost: Filtered '{0}'.", file);
                    }
                    else
                    {
                        LogLine(LogNoiseLevel.Debug, "Bifrost: Unfiltered '{0}'.", file);

                        // Just becasue there isn't an attribute saying it's binary, that doesn't mean it isn't.
                        // Scan the first block to see if it is.

                        bool is_binary = GetAttributeSet(file, "binary");

                        int bytes_read = proc_stream.Read(scratch_buffer, 0, scratch_buffer.Length);

                        if (!is_binary)
                        {
                            for (int i = 0; i < bytes_read / 2; ++i)
                            {
                                byte c = scratch_buffer[i];
                                if (c == 0)
                                {
                                    is_binary = true;
                                    break;
                                }
                            }
                        }

                        int size = bytes_read;

                        do
                        {
                            bytes_read = git_proc.StandardOutput.BaseStream.Read(scratch_buffer, 0, scratch_buffer.Length);
                            size += bytes_read;
                        }
                        while(bytes_read > 0);

                        if (git_proc.WaitForExitFail())
                        {
                            return git_proc.ExitCode;
                        }

                        int threshold;
                        if (is_binary)
                        {
                            threshold = GitConfigGetInt(BinSizeThresholdKey, DefaultBinSizeThreshold, ".gitbifrost");
                        }
                        else
                        {
                            threshold = GitConfigGetInt(TextSizeThresholdKey, DefaultTextSizeThreshold, ".gitbifrost");
                        }

                        if (threshold > -1 && size > threshold)
                        {
                            succeeded = false;
                            string type = is_binary ? "Binary" : "Text";
                            LogLine(LogNoiseLevel.Normal, "Bifrost: {0} file too big '{1}' ({2:N0} bytes).", type, file, size);

                            files_over_limit = true;
                        }
                    }
                }
            }

            LogLine(LogNoiseLevel.Normal, GetProgressString(progress_msg, staged_files.Length, staged_files.Length, ", done."));

            if (!succeeded)
            {
                if (files_over_limit)
                {
                    LogLine(LogNoiseLevel.Normal, "Bifrost: Add a filter for the file/extention or bump up your limits and don't forget to restage");
                    LogLine(LogNoiseLevel.Normal, "Bifrost: If you have updated your .gitattributes, make sure it has been staged for this commit.");
                }

                if (files_need_restaging)
                {
                    LogLine(LogNoiseLevel.Normal, "Bifrost: Files were just added to be filtered by git-bifrost. You need to restage before you can commit.");
                }

                LogLine(LogNoiseLevel.Normal, "Bifrost: Aborting commit.");
            }

            return succeeded ? Succeeded : Failed;
        }

        /*
        * A filter driver consists of a clean command and a smudge command, either of which can be left unspecified.
        * Upon checkout, when the smudge command is specified, the command is fed the blob object from its standard input,
        * and its standard output is used to update the worktree file.
        * Similarly, the clean command is used to convert the contents of worktree file upon checkin.
        */
        static int CmdFilterClean(string[] args)
        {
            string arg_filepath = null;

            if (args.Length > 1)
            {
                arg_filepath = args[1];
            }
            else
            {
                return Failed;
            }

            MemoryStream file_stream = new MemoryStream(Megabyte);

            using (Stream stdin = Console.OpenStandardInput())
            {
                stdin.CopyTo(file_stream);
                file_stream.Position = 0;
            }

            bool is_bifrost_proxy = false;

            {
                StreamReader reader = new StreamReader(file_stream);
                is_bifrost_proxy = reader.ReadLine().StartsWith(BifrostProxySignature);

                file_stream.Position = 0;
            }

            if (is_bifrost_proxy)
            {
                // Acording to the git docs, it's possible for a filter to run on the same file multiple times so we need to handle
                // the case where a proxy is passed in for a clean at some point. So far I've not seen this scenario occur,
                // so until it's clear when and why this could happen in our setup, I'll leave this fail condition here to catch it.
                LogLine(LogNoiseLevel.Normal, "Bifrost: File '{0}' is already bifrost proxy, why are you cleaning again?", arg_filepath);

                return Failed;
            }

            byte[] file_hash_bytes = SHA1.Create().ComputeHash(file_stream);
            string file_hash = BitConverter.ToString(file_hash_bytes).Replace("-", "");

            file_stream.Position = 0;

            // Give git the proxy instead of the actual file
            using (StreamWriter output_writer = new StreamWriter(Console.OpenStandardOutput()))
            {
                output_writer.WriteLine(BifrostProxySignature);
                output_writer.WriteLine(BifrostVersion);
                output_writer.WriteLine(file_hash);
                output_writer.WriteLine(file_stream.Length);
            }


            LogLine(LogNoiseLevel.Loud, " Name: {0}", arg_filepath);
            LogLine(LogNoiseLevel.Loud, " Hash: {0}", file_hash);
            LogLine(LogNoiseLevel.Loud, "Bytes: {0}", file_stream.Length);


            // Dump the real file into the local store
            string output_filename = GetFilePathFromSHA(file_hash);


            WriteToLocalStore(file_stream, output_filename);

            return Succeeded;
        }

       /*
        * A filter driver consists of a clean command and a smudge command, either of which can be left unspecified.
        * Upon checkout, when the smudge command is specified, the command is fed the blob object from its standard input,
        * and its standard output is used to update the worktree file.
        * Similarly, the clean command is used to convert the contents of worktree file upon checkin.
        */
        static int CmdFilterSmudge(string[] args)
        {
            string arg_filepath = null;

            if (args.Length > 1)
            {
                arg_filepath = args[1];
            }
            else
            {
                return Failed;
            }

            string expected_file_hash = null;
            int expected_file_size = -1;

            using (StreamReader input_reader = new StreamReader(Console.OpenStandardInput()))
            {
                string bifrost_sig = input_reader.ReadLine();

                if (!bifrost_sig.StartsWith(BifrostProxySignature))
                {
                    LogLine(LogNoiseLevel.Normal, "Bifrost: '{0}' is not a bifrost proxy file but is being smudged.", arg_filepath);
                    return Failed;
                }

                string bifrost_ver = input_reader.ReadLine();
                expected_file_hash = input_reader.ReadLine();
                expected_file_size = int.Parse(input_reader.ReadLine());
            }

            string input_filename = GetFilePathFromSHA(expected_file_hash);


            bool succeeded = false;

            var store_interfaces = GetStoreInterfaces();

            var stores = GetStores();

            LogLine(LogNoiseLevel.Debug, "Bifrost: Store count: {0}", stores.Count);

            // Walk through all the stores/interfaces and attempt to retrevie a matching file from any of them
            foreach (var store in stores)
            {
                // It's perfectly normal for some urls to be invalid uris becasue
                // different plaforms have different rules for file system uris
                Uri store_uri;
                if (!Uri.TryCreate(store["url"], UriKind.Absolute, out store_uri))
                {
                    continue;
                }

                IStoreInterface store_interface = store_interfaces[store_uri.Scheme];

                if (store_interface != null)
                {
                    if (store_interface.OpenStore(store_uri, store))
                    {
                        byte[] file_contents = store_interface.PullFile(store_uri, input_filename);

                        store_interface.CloseStore();

                        if (file_contents != null)
                        {
                            int loaded_file_size = file_contents.Length;

                            string loaded_file_hash = SHA1FromBytes(file_contents);
                            
                            LogLine(LogNoiseLevel.Loud, "    Repo File: {0}", arg_filepath);
                            LogLine(LogNoiseLevel.Loud, "   Store Name: {0}", input_filename);
                            LogLine(LogNoiseLevel.Loud, "  Expect Hash: {0}", expected_file_hash);
                            LogLine(LogNoiseLevel.Loud, "  Loaded Hash: {0}", loaded_file_hash);
                            LogLine(LogNoiseLevel.Loud, "Expected Size: {0}", expected_file_size);
                            LogLine(LogNoiseLevel.Loud, "  Loaded Size: {0}", loaded_file_size);

                            //
                            // Safety checking size and hash
                            //

                            if (expected_file_size != loaded_file_size)
                            {
                                LogLine(LogNoiseLevel.Normal, "!!!ERROR!!!");
                                LogLine(LogNoiseLevel.Normal, "File size missmatch with '{0}'", arg_filepath);
                                LogLine(LogNoiseLevel.Normal, "Store '{0}'", store_uri.AbsoluteUri);
                                LogLine(LogNoiseLevel.Normal, "Expected {0}, got {1}", expected_file_size, loaded_file_size);
                                LogLine(LogNoiseLevel.Normal, "Will try another store, but this one should be tested for integrity");
                                continue;
                            }

                            if (loaded_file_hash != expected_file_hash)
                            {
                                LogLine(LogNoiseLevel.Normal, "!!!ERROR!!!");
                                LogLine(LogNoiseLevel.Normal, "File hash missmatch with '{0}'", arg_filepath);
                                LogLine(LogNoiseLevel.Normal, "Store '{0}'", store_uri.AbsoluteUri);
                                LogLine(LogNoiseLevel.Normal, "Expected {0}, got {1}", expected_file_hash, loaded_file_hash);
                                LogLine(LogNoiseLevel.Normal, "Will try another store, but this one should be tested for integrity");
                                continue;
                            }

                            //
                            // Finally write file
                            //

                            // Put a copy in the local store
                            WriteToLocalStore(new MemoryStream(file_contents), input_filename);

                            // Give it to git to update the working directory
                            using (Stream stdout = Console.OpenStandardOutput())
                            {
                                stdout.Write(file_contents, 0, loaded_file_size);
                            }

                            succeeded = true;

                            break;
                        }
                        else
                        {
                            LogLine(LogNoiseLevel.Loud, "Bifrost: Store {0} does not contain file.", store_uri.AbsoluteUri);
                            LogLine(LogNoiseLevel.Loud, "    Repo File: {0}", arg_filepath);
                            LogLine(LogNoiseLevel.Loud, "   Store Name: {0}", input_filename);
                        }
                    }
                }
                else
                {
                    LogLine(LogNoiseLevel.Normal, "Unrecognized store type in '{0}'.", store_uri.ToString());
                }
            }

            if (!succeeded)
            {
                LogLine(LogNoiseLevel.Normal, "Bifrost: Failed to get file '{0}'.", arg_filepath);
            }

            return succeeded ? Succeeded : Failed;
        }

        static void WriteToLocalStore(Stream file_stream, string filename)
        {
            string filepath = Path.Combine(LocalStoreLocation, filename);

            string filedir = Path.GetDirectoryName(filepath);

            if (!File.Exists(filepath))
            {
                if (!Directory.Exists(filedir))
                {
                    Directory.CreateDirectory(filedir);
                }

                using (FileStream output_stream = new FileStream(filepath, FileMode.Create, FileAccess.Write))
                {
                    file_stream.CopyTo(output_stream);

                    LogLine(LogNoiseLevel.Loud, "Bifrost: Local store updated with '{0}'.", filepath);
                }
            }
            else
            {
                LogLine(LogNoiseLevel.Loud, "Bifrost: Local store update skipped");
            }
        }

        static int CmdVerify(string[] args)
        {
            string arg_username = GetArgValue(args, "--username");
            string arg_password = GetArgValue(args, "--password");
            bool arg_verbose = GetArgSet(args, "--verbose");
            string arg_store = args[args.Length - 1];

            Uri store_uri;
            if (!Uri.TryCreate(arg_store, UriKind.Absolute, out store_uri))
            {
                LogLine(LogNoiseLevel.Normal, "Bifrost: Invalid store uri '{0}'", arg_store);
                return Failed;
            }

            IStoreInterface store_interface;
            var store_interfaces = GetStoreInterfaces();
            if (!store_interfaces.TryGetValue(store_uri.Scheme, out store_interface))
            {
                LogLine(LogNoiseLevel.Normal, "Bifrost: Unsupported protocol in uri '{0}'", store_uri.AbsoluteUri);
                return Failed;
            }

            var store_data = new Dictionary<string, string>();
            store_data["url"] = store_uri.AbsoluteUri;
            store_data["username"] = arg_username;
            store_data["password"] = arg_password;

            int bad_files = 0;

            if (store_interface.OpenStore(store_uri, store_data))
            {
                // Get all reachable revisions

                List<string> revision_list = new List<string>(1000);

                using (Process git_proc = StartGit("rev-list --all"))
                {
                    string line;
                    while ((line = git_proc.StandardOutput.ReadLine()) != null)
                    {
                        revision_list.Add(line);
                    }

                    git_proc.WaitForExit();
                }

                foreach (string revision in revision_list)
                {
                    string[] revision_files = GetFilesAndStatusInRevision(revision);

                    for (int i = 0; i < revision_files.Length; i+=2)
                    {
                        string status = revision_files[i];
                        string file = revision_files[i+1];

                        string file_rev = string.Format("{0}:{1}", revision, file);

                        if (status == "X")
                        {
                            LogLine(LogNoiseLevel.Normal, "Bifrost: According to git something has gone wrong with file '{0}'. This is probably a bug in git and should be reported.", file_rev);
                            return Failed;
                        }

                        // Skip files that have been deleted
                        if (status != "D")
                        {
                            BifrostPoxy proxy = GetProxyData(file_rev);

                            if (proxy == null) continue;

                            string store_file = GetFilePathFromSHA(proxy.SHA1);

                            byte[] file_bytes = store_interface.PullFile(store_uri, store_file);

                            bool file_missing = file_bytes == null;
                            bool wrong_size = false;
                            bool bad_sha = false;

                            string file_sha = GitEmptySha;

                            if (!file_missing)
                            {
                                wrong_size = file_bytes.Length != proxy.FileSize;

                                file_sha = SHA1FromBytes(file_bytes);
                                bad_sha = file_sha != proxy.SHA1;
                            }

                            bool bad_things = file_missing | bad_sha | wrong_size;

                            if (bad_things)
                            {
                                Console.ForegroundColor = ConsoleColor.Red;
                            }

                            if (arg_verbose | bad_things)
                            {
                                Log(LogNoiseLevel.Normal, file_rev);
                            }

                            if (bad_things)
                            {
                                ++bad_files;

                                if (file_missing)
                                {
                                    Log(LogNoiseLevel.Normal, " - Missing");
                                }

                                if (wrong_size)
                                {
                                    Log(LogNoiseLevel.Normal, " - Wrong Size: Got '{0}' Expected '{1}'", file_bytes.Length, proxy.FileSize);
                                }

                                if (bad_sha)
                                {
                                    Log(LogNoiseLevel.Normal, " - Bad SHA: Got '{0}' Expected '{1}'", file_sha, proxy.SHA1);
                                }

                                Console.ResetColor();
                            }
                            else if (arg_verbose)
                            {
                                Log(LogNoiseLevel.Normal, " - OK!");
                            }

                            if (arg_verbose | bad_things)
                            {
                                LogLine(LogNoiseLevel.Normal, "");
                            }
                        }
                    }
                }

                store_interface.CloseStore();
            }
            else
            {
                LogLine(LogNoiseLevel.Normal, "Bifrost: Failed to open store '{0}'", store_uri.AbsoluteUri);
                return Failed;
            }

            return bad_files;

        }

        static int CmdHelp(string[] args)
        {
            LogLine(LogNoiseLevel.Normal, "usage: git-bifrost <command> [<args>]");
            LogLine(LogNoiseLevel.Normal, "");
            LogLine(LogNoiseLevel.Normal, "Commands:");
            LogLine(LogNoiseLevel.Normal, "   clone       Like a normal git-clone but installs git-bifrost prior to checkout.");
            LogLine(LogNoiseLevel.Normal, "   init        Installs bifrost into the specified git repository.");
            LogLine(LogNoiseLevel.Normal, "   verify      Verifies that all indexed, bifrost managed files reachable in a store and are intact.");

            return Succeeded;
        }

        static int CmdClone(string[] args)
        {
            string clone_args = string.Join(" ", args, 1, args.Length - 1);

            if (StartGit("clone", "--no-checkout", clone_args).WaitForExitSucceed(true))
            {
                string arg_directory = args.Length > 2 ? args[args.Length - 1] : Path.GetFileNameWithoutExtension(args[1].TrimEnd('/', '\\'));

                Directory.SetCurrentDirectory(arg_directory);

                InstallBifrost();

                if (StartGit("checkout").WaitForExitSucceed(true))
                {
                    return Succeeded;
                }
            }

            return Failed;
        }

        static int CmdInit(string[] args)
        {
            return InstallBifrost();
        }
            
        static int InstallBifrost()
        {
            if (!Directory.Exists(".git"))
            {
                Console.WriteLine("No git repository at '{0}'. Init a repo before using bifrost.", Directory.GetCurrentDirectory());
                return Failed;
            }

#if !(__MonoCS__)
            string filter_clean = "git-bifrost.exe filter-clean %f";
            string filter_smudge = "git-bifrost.exe filter-smudge %f";
            string hook_precommit = "git-bifrost.exe hook-pre-commit \"$@\"";
            string hook_prepush = "git-bifrost.exe hook-pre-push \"$@\"";
#else
            string filter_clean = "git-bifrost filter-clean %f";
            string filter_smudge = "git-bifrost filter-smudge %f";
            string hook_precommit = "git-bifrost hook-pre-commit \"$@\"";
            string hook_prepush = "git-bifrost hook-pre-push \"$@\"";
#endif

            if (!GitConfigSet("filter.bifrost.clean", filter_clean) ||
                !GitConfigSet("filter.bifrost.smudge", filter_smudge) ||
                !GitConfigSet("filter.bifrost.required", "true"))
            {
                return Failed;
            }

            try
            {
                File.WriteAllLines(".git/hooks/pre-commit", new string[] { "#!/bin/bash", hook_precommit });
                File.WriteAllLines(".git/hooks/pre-push", new string[] { "#!/bin/bash", hook_prepush });                
#if __MonoCS__
                Syscall.chmod(".git/hooks/pre-commit", FilePermissions.ACCESSPERMS);
                Syscall.chmod(".git/hooks/pre-push", FilePermissions.ACCESSPERMS);
                //Syscall.chmod(".git/hooks/post-checkout", FilePermissions.ACCESSPERMS);
#endif // __MonoCS__
            }
            catch
            {
                return Failed;
            }

            Console.WriteLine("Bifrost successfully installed into repo.");

            return Succeeded;
        }

        static string GetProgressString(string message, int num, int total, string suffix = null)
        {
            int percent_complete = (int)((((float)num) / total) * 100);
            return string.Format("{0}: {1}% ({2}/{3}){4}",
                message, percent_complete, num, total, suffix == null ? "" : suffix);
        }

        static string GetFilePathFromSHA(string file_hash)
        {
            // Use the first 3 hex characters as the directory names which is 4096 directories
            return String.Format("{0}/{1}/{2}/{3}.bin", file_hash[0], file_hash[1], file_hash[2], file_hash);
        }
            
        static string[] GetFilesAndStatusInRevision(string revision)
        {
            string[] revision_files;

            // Get files modified in this revision
            // format: file_status<null>file_name<null>file_status<null>file_name<null>
            using (Process git_proc = StartGit("diff-tree --no-commit-id --name-status --root -r -z", revision))
            {
                string proc_data = git_proc.StandardOutput.ReadToEnd();
                revision_files = proc_data.Split(NullChar, StringSplitOptions.RemoveEmptyEntries);

                git_proc.WaitForExit();
            }

            return revision_files;
        }

        static string SHA1FromBytes(byte[] bytes)
        {
            return BitConverter.ToString(SHA1Managed.Create().ComputeHash(bytes)).Replace("-", "");
        }

        static BifrostPoxy GetProxyData(string file_rev)
        {
            BifrostPoxy proxy = null;

            using (Process git_proc = StartGit(string.Format("cat-file blob \"{0}\"", file_rev)))
            {
                StreamReader proc_out = git_proc.StandardOutput;

                char[] proxy_sig_buffer = new char[BifrostProxySignature.Length];

                int bytes_read = proc_out.Read(proxy_sig_buffer, 0, proxy_sig_buffer.Length);

                if (bytes_read == proxy_sig_buffer.Length && new string(proxy_sig_buffer) == BifrostProxySignature)
                {
                    proc_out.ReadLine(); // Read remaining line ending

                    proxy = new BifrostPoxy(
                        file_rev,
                        proc_out.ReadLine(), // Version
                        proc_out.ReadLine(), // SHA1
                        long.Parse(proc_out.ReadLine()) // File Size
                    );
                }
                else
                {
                    // Trash the output stream after we've read our bytes. Don't bother checking the error code 
                    // becasuse git errors out when we close STDOUT before reading it completly. We only need to read
                    // the first handful of chars so, no point reading the whole file or worrying about the exit code.
                    git_proc.StandardOutput.Dispose();
                }

                git_proc.WaitForExit();
            }

            return proxy;
        }
        

        static Dictionary<string, IStoreInterface> GetStoreInterfaces()
        {
            var store_interfaces = new Dictionary<string, IStoreInterface>();

            store_interfaces[Uri.UriSchemeFile] = new StoreFileSystem();
            store_interfaces[Uri.UriSchemeFtp] = new StoreFtp();
            store_interfaces["ftps"] = new StoreFtp();
            store_interfaces["sftp"] = new StoreSftp();

            return store_interfaces;
        }

        static List<Dictionary<string, string>> GetStores()
        {
            var stores = new List<Dictionary<string, string>>(7); // Arbitrary prime number

            // Add internal store
            {
                var local = new Dictionary<string, string>();
                local["name"] = "store.BIFROST.INTERNAL"; // Unique, reserved name
                local["url"] = Path.Combine(Directory.GetCurrentDirectory(), LocalStoreLocation);

                stores.Add(local);
            }

            List<string> data_stores_text = new List<string>();

            data_stores_text.AddRange(GitConfigGetRegex(@"^store\..*", ".gitbifrost"));

            if (File.Exists(".gitbifrostuser"))
            {
                data_stores_text.AddRange(GitConfigGetRegex(@"^store\..*", ".gitbifrostuser"));
            }

            foreach (string store_text in data_stores_text)
            {
                string[] store_kv = store_text.Split(new char[]{ ' ' }, 2);

                string store_name = Path.GetFileNameWithoutExtension(store_kv[0]);
                string key = Path.GetExtension(store_kv[0]).Substring(1);

                var store_data = stores.Find((Dictionary<string, string> match) =>
                        {
                            return match["name"] == store_name;
                        });

                if (store_data == null)
                {
                    store_data = new Dictionary<string, string>();
                    store_data["name"] = store_name;

                    stores.Add(store_data);
                }

                store_data[key] = store_kv[1];
            }

            return stores;
        }

        static string GetArgValue(string[] args, string arg_key, string default_value = "")
        {
            char[] eq = new char[]{ '=' };

            foreach (string arg in args)
            {
                if (arg.StartsWith(arg_key))
                {
                    string[] tokens = arg.Split(eq, 2);
                    if (tokens.Length == 2)
                    {
                        return tokens[1];
                    }
                    else
                    {
                        return default_value;
                    }
                }
            }

            return default_value;
        }

        static bool GetArgSet(string[] args,  string flag)
        {
            return args.Contains(flag);
        }

        // Returns true if set succeeded
        static bool GitConfigSet(string key, string value, string file = null)
        {
            string fileArg = string.IsNullOrWhiteSpace(file) ? "" : "-f " + file;

            string command = string.Format("config {0} {1} \"{2}\"", fileArg, key, value);

            return StartGit(command).WaitForExitSucceed(true);
        }

        static string[] GitConfigGetRegex(string key, string file)
        {
            string fileArg = string.IsNullOrWhiteSpace(file) ? "" : "-f " + file;

            Process git_proc = StartGit("config --get-regexp", fileArg, key);

            List<string> lines = new List<string>();

            string line = null;
            while((line = git_proc.StandardOutput.ReadLine()) != null)
            {
                lines.Add(line);
            }

            git_proc.WaitForExit();
            git_proc.Dispose();

            return lines.ToArray();
        }

        static int GitConfigGetInt(string key, int defaultValue, string file)
        {
            string fileArg = string.IsNullOrWhiteSpace(file) ? "" : "-f " + file;

            Process git_proc = StartGit("config --null --get", fileArg, key);

            string value_str = git_proc.StandardOutput.ReadToEnd();

            if (git_proc.WaitForExitFail(true))
            {
                return defaultValue;
            }

            int value;
            if (int.TryParse(value_str, out value))
            {
                return value;
            }

            return defaultValue;
        }

        static string GetFilterAttribute(string file)
        {
            Process git_check_attr = StartGit(string.Format("check-attr --cached -z filter \"{0}\"", file));

            string value = git_check_attr.StandardOutput.ReadToEnd();

            if (git_check_attr.WaitForExitFail(true))
            {
                return null;
            }

            // 0 File name
            // 1 Attribute (filter)
            // 2 Value
            string[] attribute_tokens = value.Split(NullChar, StringSplitOptions.RemoveEmptyEntries);

            return attribute_tokens[2];
        }

        static bool GetAttributeSet(string file, string attribute)
        {
            Process git_check_attr = StartGit(string.Format("check-attr -z {0} \"{1}\"", attribute, file));

            string value = git_check_attr.StandardOutput.ReadToEnd();

            if (git_check_attr.WaitForExitCode(true) != 0)
            {
                return false;
            }

            // File name
            // Attribute
            // Value
            string[] attribute_tokens = value.Split(NullChar, StringSplitOptions.RemoveEmptyEntries);

            return attribute_tokens[2] == "set";
        }

        static Process StartGit(params string[] arguments)
        {
            Process process = new Process();
            ProcessStartInfo psi = process.StartInfo;
            psi.FileName = "git";
            psi.Arguments = "--no-pager " + string.Join(" ", arguments);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.EnvironmentVariables["PATH"] = psi.EnvironmentVariables["PATH"].Replace(@"\", @"\\"); // Somehow windows needs this?

            if (process.Start())
            {
                return process;
            }

            LogLine(LogNoiseLevel.Normal, "Failed to start git.");
            return null;
        }

        public static void Log(LogNoiseLevel Level, string format, params object[] arg)
        {
            if ((int)NoiseLevel >= (int)Level)
            {
                Console.Error.Write(format, arg);
            }
        }

        public static void LogLine(LogNoiseLevel Level, string format, params object[] arg)
        {
            if ((int)NoiseLevel >= (int)Level)
            {
                Console.Error.WriteLine(format, arg);
            }
        }
    }

    static class ProcessEx
    {
        public static int WaitForExitCode(this Process self, bool disposeOnExit = false)
        {
            self.WaitForExit();

            int exitCode = self.ExitCode;

            if (disposeOnExit)
            {
                self.Dispose();
            }

            return exitCode;
        }

        public static bool WaitForExitFail(this Process self, bool disposeOnExit = false)
        {
            return WaitForExitCode(self, disposeOnExit) != 0;
        }

        public static bool WaitForExitSucceed(this Process self, bool disposeOnExit = false)
        {
            return WaitForExitCode(self, disposeOnExit) == 0;
        }
    }

    static class DictionaryEx
    {
        public static string GetValue(this Dictionary<string, string> self, string key, string default_value)
        {
            string value;
            if (self.TryGetValue(key, out value))
            {
                return value;
            }

            return default_value;
        }
    }

}
