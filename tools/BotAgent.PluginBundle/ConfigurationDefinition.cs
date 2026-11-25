using System.Reflection;
using System.Runtime.Loader;
using BotAgent.Domain.Plugins;

internal static class ConfigurationDefinitionReader
{
    internal static PluginPackageManifest Read(string path, PluginPackageManifest manifest)
    {
        var loader = new DefinitionContext(Path.GetDirectoryName(path)!);
        try
        {
            var type = loader.LoadFromAssemblyPath(path).GetType(manifest.EntryType!, throwOnError: true)!;
            if (!typeof(IPluginConfigurationDefinition).IsAssignableFrom(type)) return manifest;
            if (manifest.Settings is not null || manifest.Configuration is not null)
                throw new InvalidDataException("bundle_configuration_template_conflict");
            // These are trusted local build inputs. No runtime lifecycle method is called.
            var owner = (IPluginConfigurationDefinition)Activator.CreateInstance(type)!;
            var definition = owner.ConfigurationDefinition;
            var candidate = manifest with { Settings = PluginSettingValidation.ReadSchema(definition.Settings),
                Configuration = definition.Configuration };
            BotAgent.Domain.Configuration.LegacyPluginConfigurationFields.Validate(candidate);
            return candidate;
        }
        finally { loader.Unload(); }
    }

    private sealed class DefinitionContext(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.FullName == typeof(IBotPlugin).Assembly.FullName) return typeof(IBotPlugin).Assembly;
            if (name.FullName == typeof(IPluginConfigurationDefinition).Assembly.FullName)
                return typeof(IPluginConfigurationDefinition).Assembly;
            var path = Path.Combine(directory, name.Name + ".dll");
            try { return LoadFromAssemblyPath(path); }
            catch (FileNotFoundException error) when (error.FileName == path)
            {
                // Missing local copies may still resolve through the default runtime context.
                return null;
            }
        }
    }
}
