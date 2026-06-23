using System;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.PathConstruction;

class Build : NukeBuild
{
    public static int Main () => Execute<Build>(x => x.Convert);

    AbsolutePath SourceDirectory => RootDirectory / "Service Manual";
    AbsolutePath OutputDirectory => RootDirectory / "artifacts";

    Target Convert => _ => _
        .Executes(() =>
        {
            var converter = new Converter(SourceDirectory,  OutputDirectory);
            converter.Convert();
        });

}
