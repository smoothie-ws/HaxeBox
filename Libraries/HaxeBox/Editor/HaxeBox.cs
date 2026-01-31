using System;
using System.IO;
using Sandbox;
using Editor;

public static class HaxeBox {
    private static AutoBuildService? buildService;

    [Event("editor.created")]
    private static void OnEditorCreated( EditorMainWindow _ ) {
        var projectDir = Project.Current.GetRootPath();

        // generate externs
        try {
            var outRoot =  Path.Combine(projectDir, ".haxe", "extern", "sbox").Replace("\\", "/");
            var msg = ExternGen.GenerateFromRuntime(outRoot);
            Log.Info(msg);
        } catch (Exception e) {
            Log.Info(e.ToString());
            throw;
        }

        // run service
        buildService ??= new AutoBuildService();
        buildService.Start(projectDir, "haxe", Path.Combine("code", "haxe"));
    }

    [Event("app.exit")]
    private static void OnAppExit() {
        buildService?.Dispose();
        buildService = null;
    }
}
