if(args.Length!=4) throw new ArgumentException("mode input support output");
Launcher.Adapters.Rewriter.Prepare(args[0],args[1],args[2],args[3]);
