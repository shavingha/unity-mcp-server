using UnityEditor;
using UnityEngine;
using System.IO;

namespace Nurture.MCP.Editor
{
    [CreateAssetMenu(
        fileName = "Assets/Settings/UnityMCPSettings.asset",
        menuName = "MCP/Settings"
    )]
    public class Settings : ScriptableObject
    {
        [SerializeField]
        [Tooltip("Additional assemblies to always include when compiling generated scripts.")]
        private string[] alwaysIncludedAssemblies;

        public string[] AlwaysIncludedAssemblies => alwaysIncludedAssemblies;

        private const string SettingsPath = "Assets/Settings/UnityMCPSettings.asset";
        private const string SettingsDirectory = "Assets/Settings";

        public static Settings Instance
        {
            get
            {
                var settings = AssetDatabase.LoadAssetAtPath<Settings>(SettingsPath);
                if (settings == null)
                {
                    // 确保目录存在
                    if (!Directory.Exists(SettingsDirectory))
                    {
                        Directory.CreateDirectory(SettingsDirectory);
                        AssetDatabase.Refresh();
                    }
                    
                    settings = CreateInstance<Settings>();
                    AssetDatabase.CreateAsset(settings, SettingsPath);
                    AssetDatabase.SaveAssets();
                }
                return settings;
            }
        }
    }
}
