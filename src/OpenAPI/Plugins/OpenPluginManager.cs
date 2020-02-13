﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using log4net;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using OpenAPI.Utils;

namespace OpenAPI.Plugins
{
    public class OpenPluginManager
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(OpenPluginManager));

        private ConcurrentDictionary<string, Assembly> AssemblyReferences { get; }
        private Dictionary<Assembly, LoadedAssembly> LoadedAssemblies { get; }
		private ConcurrentDictionary<Type, object> References { get; }
        private readonly object _pluginLock = new object();

        private OpenAPI Parent { get; }
		private Assembly HostAssembly { get; }
		
        public OpenPluginManager(OpenAPI parent)
        {
            Parent = parent;
			HostAssembly = Assembly.GetAssembly(typeof(OpenPluginManager));

            AssemblyReferences = new ConcurrentDictionary<string, Assembly>();
            LoadedAssemblies = new Dictionary<Assembly, LoadedAssembly>();
			References = new ConcurrentDictionary<Type, object>();
			
			foreach (var referencedAssemblies in AppDomain.CurrentDomain.GetAssemblies())
	        {
		        if (!AssemblyReferences.ContainsKey(referencedAssemblies.GetName().Name))
		        {
					AssemblyReferences.TryAdd(referencedAssemblies.GetName().Name, referencedAssemblies);
					
					foreach (var referenced in referencedAssemblies.GetReferencedAssemblies())
					{
						if (!AssemblyReferences.ContainsKey(referenced.Name))
						{
							var newlyLoaded = Assembly.Load(referenced);
							AssemblyReferences.TryAdd(referenced.Name, newlyLoaded);
						}
					}
		        }
	        }

	        AppDomain.CurrentDomain.AssemblyResolve += PluginManagerOnAssemblyResolve;
        }

	    private Assembly PluginManagerOnAssemblyResolve(object sender, ResolveEventArgs args)
	    {
		    try
		    {
			  //  AssemblyName name = new AssemblyName(args.Name);
				AssemblyNameReference name = AssemblyNameReference.Parse(args.Name);
			    if (IsLoaded(name, out Assembly loadedPluginAssembly))
			    {
				    lock (_pluginLock)
				    {
					    if (!AssemblyReferences.ContainsKey(name.Name))
					    {
						    AssemblyReferences.TryAdd(name.Name, loadedPluginAssembly);
					    }
				    }

				    return loadedPluginAssembly;
			    }

			    string rootPath = "";
			    if (args.RequestingAssembly != null && !string.IsNullOrWhiteSpace(args.RequestingAssembly.Location))
			    {
				    rootPath = Path.GetDirectoryName(args.RequestingAssembly.Location);
			    }

			    Assembly result = null;
			    if (TryFindAssemblyPath(name, rootPath, out string resultPath))
			    {
				    result = Assembly.LoadFile(resultPath);
			    }
			    else
			    {
				    var assembly = AppDomain.CurrentDomain.GetAssemblies()
					    .FirstOrDefault(x => x.GetName().Name == args.Name);
				    if (assembly != null) result = assembly;
			    }

			    if (result != null)
			    {
				    AssemblyReferences.TryAdd(name.Name, result);
			    }
			    else
			    {
				    Log.Warn($"Failed to resolve assembly: {name}");
			    }

			    return result;
		    }
		    catch (Exception ex)
		    {
			    Log.Error($"Failed to resolve!", ex);
			    return null;
		    }
	    }

	    internal bool TryFindAssemblyPath(AssemblyNameReference name, string rootPath, out string resultingPath)
	    {
		    AssemblyDependencyResolver resolver = new AssemblyDependencyResolver(rootPath);
		    var p = resolver.ResolveAssemblyToPath(new AssemblyName(name.ToString()));
		    if (p != null)
		    {
			    resultingPath = p;
			    return true;
		    }
			    
		    string dllName = name.Name + ".dll";

		    var assemblyLocation = rootPath;

			string file = Path.Combine(assemblyLocation, dllName);

			string result = null;
			if (CompareFileToAssemblyName(file, name) == FileAssemblyComparisonResult.Match)
			{
				result = file;
			}
			else
			{
				string lastPath = _lastPath;
				if (!string.IsNullOrEmpty(lastPath))
				{
					file = Path.Combine(lastPath, dllName);
					if (CompareFileToAssemblyName(file, name) == FileAssemblyComparisonResult.Match)
					{
						result = file;
					}
				}

				string executingPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
				string callingAssembliesPath = Path.GetDirectoryName(Assembly.GetCallingAssembly().Location);

				if (result == null && executingPath != null)
				{
					file = Path.Combine(executingPath, dllName);
					if (File.Exists(Path.Combine(executingPath, dllName)))
					{
						result = file;
					}
				}

				if (result == null && callingAssembliesPath != null)
				{
					file = Path.Combine(callingAssembliesPath, dllName);
					if (CompareFileToAssemblyName(file, name) == FileAssemblyComparisonResult.Match)
					{
						result = file;
					}
				}

				/*AppDomain resolverDomain = AppDomain.CreateDomain("ResolverDomain");
				try
				{
					Assembly loaded = resolverDomain.Load(new AssemblyName(name.Name));
					result = loaded.Location;
				}
				catch
				{

				}
				AppDomain.Unload(resolverDomain);*/
			}

		    if (string.IsNullOrWhiteSpace(result))
		    {
			    resultingPath = default(string);
			    return false;
			}
		    else
		    {
			    resultingPath = result;
			    return true;
		    }
		}

	    private enum FileAssemblyComparisonResult
	    {
			FileNotFound,
			NotEqual,
			Match
	    }

	    private FileAssemblyComparisonResult CompareFileToAssemblyName(string file, AssemblyNameReference name)
	    {
		    if (!File.Exists(file))
			    return FileAssemblyComparisonResult.FileNotFound;

		    var module = ModuleDefinition.ReadModule(file);
			//AssemblyName fileAssemblyName = AssemblyName.GetAssemblyName(file);
				// if (AssemblyName.ReferenceMatchesDefinition(fileAssemblyName, new AssemblyName(name.FullName)))
			if (name.Name.Equals(module.Assembly.Name.Name, StringComparison.InvariantCultureIgnoreCase))
		    {
			    return FileAssemblyComparisonResult.Match;
			}
		    else
		    {
			    return FileAssemblyComparisonResult.NotEqual;
		    }
	    }

	    private string _lastPath = null;
	    public void DiscoverPlugins(string path)
        {
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException("Directory not found: " + path);

			_lastPath = path;

			List<Assembly> loadedAssemblies = new List<Assembly>();

	        int processed = 0;
            string[] files = Directory.GetFiles(path, "*.dll", SearchOption.AllDirectories);
            foreach (string file in files)
            {
                try
                {
	                path = Path.GetDirectoryName(file);

	                Assembly[] result;
                    ProcessFile(path, file, out result);
	                processed++;

					if (result == null) 
						continue;

	                foreach (var assembly in result)
	                {
						if (!loadedAssemblies.Contains(assembly))
							loadedAssemblies.Add(assembly);
	                }
                }
				catch(BadImageFormatException ex)
	            {
					if (Log.IsDebugEnabled)
						Log.Debug($"File is not a .NET Assembly ({file})", ex);
	            }
                catch (Exception ex)
                {
                    Log.Error($"Failed loading \"{file}\"", ex);
                }
            }

			Log.Info($"Loaded {loadedAssemblies.Count} assemblies from {processed} processed files.");

			List<OpenPlugin> plugins = new List<OpenPlugin>();
	        foreach (var assembly in loadedAssemblies)
	        {
				if (assembly != null)
		        if (LoadAssembly(assembly, out OpenPlugin[] pluginInstances, out Assembly[] requiredAssemblies))
		        {
			        LoadedAssemblies.Add(assembly, new LoadedAssembly(assembly, pluginInstances, requiredAssemblies, path));

					if (pluginInstances.Length > 0)
			        {
						plugins.AddRange(pluginInstances);
			        }
		        }
	        }

			Log.Info($"Found {plugins.Count} plugins");

	        int enabled = 0;
	        foreach (var plugin in plugins)
	        {
		        try
		        {
					plugin.Enabled(Parent);
			        enabled++;
		        }
		        catch (Exception ex)
		        {
					Log.Error($"Error occured while enabling plugin!", ex);
		        }
	        }

	        Log.Info($"Enabled {enabled} plugins!");

	        
        }

	    private bool IsLoaded(AssemblyNameReference name, out Assembly outAssembly)
	    {
		    Assembly[] loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();

		    Assembly ooutAssembly =
			    loadedAssemblies.FirstOrDefault(x => x != null && x.GetName().Name
			   	    .Equals(name.Name, StringComparison.InvariantCultureIgnoreCase));

		    if (ooutAssembly != null)
		    {
			    outAssembly = ooutAssembly;
			    return true;
		    }
		    outAssembly = null;
		    return false;
	    }

	    private bool ReferencesHost(ModuleDefinition assembly)
	    {
		    var hostName = HostAssembly.GetName();

			return assembly.AssemblyReferences
			    .Any(x => x.Name.Equals(hostName.Name, StringComparison.InvariantCultureIgnoreCase));
	    }

	    private bool ReferencesHost(Assembly assembly)
	    {
		    var hostName = HostAssembly.GetName();

		    return assembly.GetReferencedAssemblies()
			    .Any(x => x.Name.Equals(hostName.Name, StringComparison.InvariantCultureIgnoreCase));
	    }

		private void ProcessFile(string directory, string file, out Assembly[] pluginAssemblies)
        {
	        pluginAssemblies = null;

	       // domain = AppDomain.CurrentDomain;
	        //domain = AppDomain.CreateDomain("OpenAPI.PluginManager.Plugin");

		        //	var proxy = Proxy.CreateProxy(domain);

			List<Assembly> assemblies = new List<Assembly>();

            lock (_pluginLock)
            {
                if (!File.Exists(file))
                    throw new FileNotFoundException("File not found: " + file);

	            try
	            {
		            //var assemblyName = AssemblyName.GetAssemblyName(file);
		            var module = ModuleDefinition.ReadModule(file);

					AssemblyNameReference assemblyName = module.Assembly.Name;
		            if (IsLoaded(assemblyName, out Assembly _))
		            {
			            return;
		            }

		            if (AssemblyReferences.ContainsKey(assemblyName.Name))
			            return;

					if (!ReferencesHost(module))
			            return;

		            if (TryResolve(directory, module, out Assembly[] loadedReferences))
		            {
			            foreach (var reference in loadedReferences)
			            {
				            if (!assemblies.Contains(reference) && ReferencesHost(reference))
				            {
					            assemblies.Add(reference);
				            }
			            }

			           // var real = proxy.GetAssembly(file); // Assembly.Load(assemblyBytes);
			            var real = Assembly.LoadFrom(file);
			            assemblies.Add(real);

			            AssemblyReferences.TryAdd(assemblyName.Name, real);
					}
		            else
		            {
						Log.Warn($"Could not resolve all references for \"{module.Name}\"");
		            }
	            }
	            catch (Exception ex)
	            {
		            if (!(ex is BadImageFormatException))
			            Log.Error($"Could not load assembly as OpenPlugin (File: {file})", ex);
	            }
	            finally
	            {
					
	            }
            }

	        pluginAssemblies = assemblies.ToArray();
        }

		private bool TryResolve( string path, ModuleDefinition module, out Assembly[] assemblies)
		{
			//var proxy = Proxy.CreateProxy(domain);
			List<AssemblyNameReference> assemblyNames = module.AssemblyReferences.ToList();

			Dictionary<AssemblyNameReference, Assembly> resolvedAssemblies = new Dictionary<AssemblyNameReference, Assembly>();
			Dictionary<AssemblyNameReference, string> resolvedPaths = new Dictionary<AssemblyNameReference, string>();
		    foreach (var assemblyName in assemblyNames)
		    {
			    if (IsLoaded(assemblyName, out Assembly loadedAssembly) || AssemblyReferences.TryGetValue(assemblyName.Name, out loadedAssembly))
			    {
					resolvedAssemblies.Add(assemblyName, loadedAssembly);
					continue;
			    }

			    try
			    {
				    if (TryFindAssemblyPath(assemblyName, path, out string resultPath))
				    {
					    resolvedPaths.Add(assemblyName, resultPath);
				    }
				    else
				    {
					    Log.Warn($"Plugin \"{module.FileName}\" requires \"{assemblyName}\" but it could not be found.");
					  //  assemblies = default(Assembly[]);
					   // return false;
					}
				}
			    catch(Exception e)
			    {
				    Log.Warn($"Could not find path for {assemblyName} - {e.ToString()}");
					assemblies = default(Assembly[]);
				    return false;
			    }
		    }

		    foreach (var resolved in resolvedPaths)
		    {
			    try
			    {
				    var assembly = Assembly.LoadFrom(resolved.Value);
					resolvedAssemblies.Add(resolved.Key, assembly);
				    AssemblyReferences.TryAdd(resolved.Key.Name, assembly);
			    }
			    catch (Exception ex)
			    {
					Log.Error($"Failed to load assembly {resolved.Key} even tho its path was found!", ex);

				    assemblies = default(Assembly[]);
				    return false;
				}
		    }

		    assemblies = resolvedAssemblies.Values.ToArray();
		    return true;
	    }

		private readonly Type _requiredType = typeof(OpenPlugin);
	    private bool LoadAssembly(Assembly assembly, out OpenPlugin[] loaded, out Assembly[] referencedAssemblies)
	    {
		    try
		    {
				var refAssemblies = new List<Assembly>();
			    var plugins = new List<OpenPlugin>();

			    Type[] types = assembly.GetExportedTypes();
			    foreach (Type type in types.Where(x => _requiredType.IsAssignableFrom(x) && !x.IsAbstract && x.IsClass))
			    {
				    ConstructorInfo ctor = type.GetConstructor(Type.EmptyTypes);
				    if (ctor != null)
				    {
					    OpenPlugin plugin;
					    try
					    {
						    plugin = (OpenPlugin) ctor.Invoke(null);
					    }
					    catch(Exception ex)
					    {
						    plugin = null;
						    Log.Error("An error has occurred", ex);
					    }

					    if (plugin != null)
					    {
							plugins.Add(plugin);
						}
				    }
				    else
				    {
					    foreach (ConstructorInfo constructor in type.GetConstructors())
					    {
							List<Assembly> assembliesReferenced = new List<Assembly>();
							List<object> parameters = new List<object>();
						    foreach (ParameterInfo argument in constructor.GetParameters())
						    {
							    if (argument.ParameterType == typeof(OpenAPI))
							    {
								    parameters.Add(Parent);
									continue;
							    }

							    if (References.TryGetValue(argument.ParameterType, out object arg))
							    {
									parameters.Add(arg);

								    Assembly argsAssembly = arg.GetType().Assembly;
								    if (!assembliesReferenced.Contains(argsAssembly))
								    {
									    assembliesReferenced.Add(argsAssembly);
								    }
								    continue;
							    }

							    foreach (LoadedAssembly loadedPlugin in LoadedAssemblies.Values)
							    {
								    foreach (OpenPlugin loadedPlug in loadedPlugin.PluginInstances)
								    {
									    if (argument.ParameterType == loadedPlug.GetType())
									    {
										    parameters.Add(loadedPlug);

										    if (loadedPlugin.Assembly != assembly) //If the instance of the type is not from the assembly being loaded, add the type's assembly to a list of dependencies
										    {
											    if (!assembliesReferenced.Contains(loadedPlugin.Assembly))
											    {
												    assembliesReferenced.Add(loadedPlugin.Assembly);
											    }
										    }
									    }
								    }
							    }
						    }

						    if (parameters.Count == constructor.GetParameters().Length)
						    {
							    var plugin = (OpenPlugin) constructor.Invoke(parameters.ToArray());
							    foreach (Assembly reference in assembliesReferenced)
							    {
								    if (!refAssemblies.Contains(reference))
								    {
									    refAssemblies.Add(reference);
								    }
							    }

								plugins.Add(plugin);
								//Log.Info($"Plugin instance created: {plugin.GetType().FullName}");
							    break;
							}
						    else
						    {
								Log.Warn($"Could not call constructor for {constructor.ToString()}");
						    }
					    }
				    }
			    }

			    if (plugins.Count > 0)
			    {
				    referencedAssemblies = refAssemblies.ToArray();
				    loaded = plugins.ToArray();
				    return true;
			    }
		    }
		    catch(Exception ex)
		    {
			    Log.Error($"Could not load assembly ({assembly.FullName})", ex);
		    }

			loaded = new OpenPlugin[0];
			referencedAssemblies = new Assembly[0];
		    return false;
	    }

	    private void FindRequiredAssemblies(Assembly assembly, List<Assembly> assemblies)
	    {
		    
	    }

        public void UnloadPluginAssembly(Assembly pluginAssembly)
        {
            lock (_pluginLock)
            {
	            if (!LoadedAssemblies.TryGetValue(pluginAssembly, out LoadedAssembly assemblyPlugins))
                {
                    Log.Error($"Error unloading all plugins for assembly: No plugins found/loaded.");
                    return;
                }

	            //Unload all assemblies that referenced this plugin's assembly
	            foreach (Assembly referencedAssembly in assemblyPlugins.AssemblyReferences)
	            {
		            if (LoadedAssemblies.ContainsKey(referencedAssembly))
		            {
			            UnloadPluginAssembly(referencedAssembly);
		            }
	            }

				//Remove all this assembly's type instances from list of references
	            foreach (Type type in pluginAssembly.GetTypes())
	            {
		            if (References.ContainsKey(type))
		            {
			            References.TryRemove(type, out var _);
		            }
	            }

				//Unload all plugin instances
				foreach (OpenPlugin plugin in assemblyPlugins.PluginInstances)
                {
                    UnloadPlugin(plugin);
                }
            }
        }

        private void UnloadPlugin(OpenPlugin plugin)
        {
            lock (_pluginLock)
            {
                plugin.Disabled(Parent);

                Assembly assembly = plugin.GetType().Assembly;

	            if (LoadedAssemblies.TryGetValue(assembly, out LoadedAssembly assemblyPlugins))
	            {
		            assemblyPlugins.PluginInstances.Remove(plugin);
					Parent.CommandManager.UnloadCommands(plugin);
					
		            if (!assemblyPlugins.PluginInstances.Any())
		            {
			            LoadedAssemblies.Remove(assembly);
		            }
				}
	            else
	            {
					Log.Error($"Error unloading plugin {plugin.GetType()}: Assembly has no loaded plugins");
	            }
            }
        }

        public void UnloadAll()
        {
            lock (_pluginLock)
            {
                foreach (KeyValuePair<string, Assembly> pluginAssembly in AssemblyReferences.ToArray())
                {
                    if (LoadedAssemblies.TryGetValue(pluginAssembly.Value, out LoadedAssembly assembly))
                    {
                        foreach (OpenPlugin pluginInstance in assembly.PluginInstances)
                        {
	                        UnloadPlugin(pluginInstance);
                        }
                     //   LoadedAssemblies.Remove(pluginAssembly.Value);
                    }

                    AssemblyReferences.TryRemove(pluginAssembly.Key, out Assembly _);
                }
            }
        }

	    public void SetReference<TType>(TType reference)
	    {
		    if (!References.TryAdd(typeof(TType), reference))
		    {
			    throw new Exception("Type reference already set!");
		    }
	    }

	    public bool TryGetReference(Type type, out object result)
	    {
		    return References.TryGetValue(type, out result);
	    }
	    
	    public bool TryGetReference<TType>(out TType result)
	    {
		    if (References.TryGetValue(typeof(TType), out object value))
		    {
			    result = (TType) value;
			    return true;
		    }

		    result = default(TType);
		    return false;
	    }

	    public LoadedPlugin[] GetLoadedPlugins()
	    {
		    return LoadedAssemblies.Values.SelectMany(x =>
		    {
			    string[] referencedPlugins = GetReferencedPlugins(x);
			    return x.PluginInstances.Select((p) =>
			    {
				    OpenPluginInfo info = p.Info;

				    return new LoadedPlugin(p, info, true)
				    {
					    Dependencies = referencedPlugins
					};
			    });
		    }).ToArray();
	    }

	    private string[] GetReferencedPlugins(LoadedAssembly assembly)
	    {
			List<string> references = new List<string>();

		    foreach (var asm in assembly.AssemblyReferences)
		    {
			    if (LoadedAssemblies.TryGetValue(asm, out LoadedAssembly reference))
			    {
				    foreach (var plugin in reference.PluginInstances)
				    {
					    references.Add(plugin.GetType().AssemblyQualifiedName);
				    }
			    }
		    }

		    return references.ToArray();
	    }
	}
}
