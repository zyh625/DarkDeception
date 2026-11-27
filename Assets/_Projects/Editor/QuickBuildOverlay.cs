using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Overlays;
using UnityEditor.SceneManagement;
using UnityEditor.Toolbars;
using UnityEngine;

namespace DarkDeception.EditorTools
{
    /// <summary>
    /// DarkDeception Windows 快捷打包工具：
    /// - 在 Scene 视图顶部工具栏挂载「快速 Build / Build并运行 / 打开目录」按钮；
    /// - 同步提供菜单栏 Tools/DarkDeception 快捷入口与命令行 CLI 打包接口。
    /// </summary>
    public static class QuickBuildUtility
    {
        private const string BuildOutputRelPath = "Builds/DarkDeception.exe";

        [MenuItem("Tools/DarkDeception/快速 Build (Windows) %#b", false, 1)]
        public static void BuildWindowsInteractive()
        {
            RunWindowsBuild(autoRun: false, fromCli: false);
        }

        [MenuItem("Tools/DarkDeception/Build 并运行 (Windows)", false, 2)]
        public static void BuildAndRunWindowsInteractive()
        {
            RunWindowsBuild(autoRun: true, fromCli: false);
        }

        [MenuItem("Tools/DarkDeception/打开 Builds 文件夹", false, 20)]
        public static void OpenBuildsFolder()
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string buildsDir = Path.Combine(projectRoot ?? ".", "Builds");
            if (!Directory.Exists(buildsDir))
            {
                Directory.CreateDirectory(buildsDir);
            }
            EditorUtility.RevealInFinder(buildsDir);
        }

        /// <summary>
        /// 命令行批量打包入口：Unity.exe -batchmode -quit -projectPath ... -executeMethod DarkDeception.EditorTools.QuickBuildUtility.BuildWindowsFromCLI
        /// </summary>
        public static void BuildWindowsFromCLI()
        {
            bool ok = RunWindowsBuild(autoRun: false, fromCli: true);
            if (!ok)
            {
                EditorApplication.Exit(1);
            }
        }

        public static bool RunWindowsBuild(bool autoRun, bool fromCli)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[QuickBuild] 请先退出 Play Mode 再执行打包。");
                NotifySceneView("请先退出运行状态再打包");
                return false;
            }

            if (!fromCli && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.Log("[QuickBuild] 已取消打包。");
                return false;
            }

            string[] enabledScenes = EditorBuildSettings.scenes
                .Where(s => s != null && s.enabled && !string.IsNullOrEmpty(s.path) && File.Exists(s.path))
                .Select(s => s.path)
                .ToArray();

            if (enabledScenes.Length == 0)
            {
                Debug.LogError("[QuickBuild] EditorBuildSettings 中没有可用的已启用场景！");
                NotifySceneView("打包失败：无启用场景");
                return false;
            }

            string projectRoot = Path.GetDirectoryName(Application.dataPath) ?? ".";
            string outputPath = Path.Combine(projectRoot, BuildOutputRelPath);
            string outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = enabledScenes,
                locationPathName = outputPath,
                target = BuildTarget.StandaloneWindows64,
                options = autoRun ? BuildOptions.AutoRunPlayer : BuildOptions.None
            };

            Debug.Log($"[QuickBuild] 开始构建 Windows64 -> {outputPath}（场景数：{enabledScenes.Length}）");
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                double sizeMb = summary.totalSize / (1024.0 * 1024.0);
                string msg = $"打包成功：{sizeMb:F1} MB（耗时 {summary.totalTime.TotalSeconds:F1}s）";
                Debug.Log($"[QuickBuild] {msg}\n输出路径：{outputPath}");
                NotifySceneView(msg);
                return true;
            }

            string failMsg = $"打包失败：{summary.result}（错误数：{summary.totalErrors}）";
            Debug.LogError($"[QuickBuild] {failMsg}");
            NotifySceneView(failMsg);
            return false;
        }

        private static void NotifySceneView(string message)
        {
            if (SceneView.lastActiveSceneView != null)
            {
                SceneView.lastActiveSceneView.ShowNotification(new GUIContent(message));
            }
        }
    }

    [Overlay(typeof(SceneView), "darkdeception-quick-build-toolbar", "快速 Build", true,
        defaultDockZone = DockZone.TopToolbar, defaultDockPosition = DockPosition.Top)]
    public class QuickBuildSceneOverlay : ToolbarOverlay
    {
        public QuickBuildSceneOverlay()
            : base(QuickBuildButton.Id, QuickBuildRunButton.Id, OpenBuildsDirButton.Id)
        {
        }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    public class QuickBuildButton : EditorToolbarButton
    {
        public const string Id = "DarkDeception/QuickBuild";

        public QuickBuildButton()
        {
            text = "快速 Build";
            tooltip = "一键打包 Windows 64 位版本至 Builds/DarkDeception.exe (Ctrl+Shift+B)";
            icon = EditorGUIUtility.IconContent("BuildSettings.Standalone").image as Texture2D;
            clicked += () => QuickBuildUtility.BuildWindowsInteractive();
        }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    public class QuickBuildRunButton : EditorToolbarButton
    {
        public const string Id = "DarkDeception/QuickBuildAndRun";

        public QuickBuildRunButton()
        {
            text = "Build 并运行";
            tooltip = "打包 Windows 64 位版本并立即启动 DarkDeception.exe";
            icon = EditorGUIUtility.IconContent("PlayButton").image as Texture2D;
            clicked += () => QuickBuildUtility.BuildAndRunWindowsInteractive();
        }
    }

    [EditorToolbarElement(Id, typeof(SceneView))]
    public class OpenBuildsDirButton : EditorToolbarButton
    {
        public const string Id = "DarkDeception/OpenBuildsDir";

        public OpenBuildsDirButton()
        {
            text = "Builds";
            tooltip = "在资源管理器中打开 Builds 输出目录";
            icon = EditorGUIUtility.IconContent("FolderOpened Icon").image as Texture2D;
            clicked += () => QuickBuildUtility.OpenBuildsFolder();
        }
    }
}
