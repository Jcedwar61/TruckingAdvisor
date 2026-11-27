// Signatures of SimHub's plugin API as SimHub.Plugins.dll declares them (checked against SimHub 9.x).
// Bodies are never run: SimHub's real assembly is loaded in its place.
using System;

namespace SimHub.Plugins
{
    /// <summary>SimHub's plugin host. The plugin reaches everything else on it by reflection.</summary>
    public class PluginManager
    {
        public static PluginManager GetInstance() { throw new NotSupportedException("SimHub API stub"); }
    }

    /// <summary>What SimHub loads from a DLL in its folder (any non-abstract type with a parameterless constructor).</summary>
    public interface IPlugin
    {
        PluginManager PluginManager { set; }
        void Init(PluginManager pluginManager);
        void End(PluginManager pluginManager);
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class PluginNameAttribute : Attribute
    {
        public string name;
        public PluginNameAttribute(string name) { this.name = name; }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class PluginDescriptionAttribute : Attribute
    {
        public string description;
        public PluginDescriptionAttribute(string description) { this.description = description; }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class PluginAuthorAttribute : Attribute
    {
        public string name;
        public PluginAuthorAttribute(string name) { this.name = name; }
    }
}
