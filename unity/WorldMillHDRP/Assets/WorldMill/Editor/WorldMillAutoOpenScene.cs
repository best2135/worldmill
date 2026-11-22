using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace WorldMill.Editor
{
    /// <summary>
    /// Automatically opens Assets/Scenes/WorldMillHDRP.unity when opening the project
    /// so you never land in an empty Untitled scene.
    /// </summary>
    [InitializeOnLoad]
    public static class WorldMillAutoOpenScene
    {
        static WorldMillAutoOpenScene()
        {
            if (UnityEngine.Application.isBatchMode) return;
            EditorApplication.delayCall += () =>
            {
                var active = SceneManager.GetActiveScene();
                if (string.IsNullOrEmpty(active.path) && System.IO.File.Exists(WorldMillHdrpSetup.ScenePath))
                {
                    EditorSceneManager.OpenScene(WorldMillHdrpSetup.ScenePath, OpenSceneMode.Single);
                }
            };
        }
    }
}
