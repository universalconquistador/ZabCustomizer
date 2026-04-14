using Dalamud.Plugin.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ZabCustomizer;

public delegate void CustomizeDefinitionFilesChanged(IEnumerable<string> addedDefinitionFiles, IEnumerable<string> modifiedDefinitionFiles, IEnumerable<string> removedDefinitionFiles);

/// <summary>
/// Watches the Penumbra mod directory for all customize.json files.
/// </summary>
public class DefinitionManager : IDisposable
{
    private readonly IPluginLog _log;

    // Mod directory to customize definition
    private ConcurrentDictionary<string, CustomizeDefinition> _definitions = new();
    private readonly FileSystemWatcher _fileSystemWatcher;

    public event CustomizeDefinitionFilesChanged? DefinitionFilesChanged;

    public string? PenumbraModDirectory
    {
        get => field;
        set
        {
            if (value != field)
            {
                field = value;

                if (value != _fileSystemWatcher.Path || (!string.IsNullOrEmpty(value) && !_fileSystemWatcher.EnableRaisingEvents))
                {
                    if (string.IsNullOrEmpty(value))
                    {
                        _fileSystemWatcher.EnableRaisingEvents = false;

                        var oldDefinitions = Interlocked.Exchange(ref _definitions, new());
                        DefinitionFilesChanged?.Invoke(Enumerable.Empty<string>(), Enumerable.Empty<string>(), oldDefinitions.Keys);
                    }
                    else
                    {
                        _fileSystemWatcher.Path = value;
                        _fileSystemWatcher.EnableRaisingEvents = true;
                        RescanDefinitionFiles(value);
                    }
                }
            }
        }
    }

    public DefinitionManager(IPluginLog log)
    {
        _log = log;

        _fileSystemWatcher = new();
        _fileSystemWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
        _fileSystemWatcher.Changed += this.OnDefinitionFileChanged;
        _fileSystemWatcher.Created += this.OnDefinitionFileCreated;
        _fileSystemWatcher.Deleted += this.OnDefinitionFileDeleted;
        _fileSystemWatcher.Renamed += this.OnDefinitionFileRenamed;
        _fileSystemWatcher.IncludeSubdirectories = true;
    }

    private void OnDefinitionFileRenamed(object sender, RenamedEventArgs e)
    {
        IEnumerable<string>? addedDefinitions = null;
        IEnumerable<string>? removedDefinitions = null;

        _log.Debug("RENAME: {old} -> {new}", e.OldFullPath, e.FullPath);

        if (e.OldName != null && Path.GetFileName(e.Name) == CustomizeDefinition.Filename && Path.GetDirectoryName(e.OldFullPath) is string oldDirectory && _definitions.TryRemove(oldDirectory, out _))
        {
            removedDefinitions = new string[] { oldDirectory };
        }

        if (e.Name != null && Path.GetFileName(e.Name) == CustomizeDefinition.Filename && Path.GetDirectoryName(e.FullPath) is string newDirectory && TryLoadDefinition(e.FullPath, out var definition))
        {
            addedDefinitions = new string[] { newDirectory };
            _definitions[newDirectory] = definition;
        }

        if (addedDefinitions != null || removedDefinitions != null)
        {
            // .json was renamed
            DefinitionFilesChanged?.Invoke(addedDefinitions ?? Enumerable.Empty<string>(), Enumerable.Empty<string>(), removedDefinitions ?? Enumerable.Empty<string>());
        }
        else if (_definitions.TryRemove(e.OldFullPath, out var oldDefinition))
        {
            // folder itself was renamed
            _definitions[e.FullPath] = oldDefinition;
            DefinitionFilesChanged?.Invoke([e.FullPath], Enumerable.Empty<string>(), [e.OldFullPath]);
        }
    }

    private void OnDefinitionFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (Path.GetFileName(e.Name) == CustomizeDefinition.Filename)
        {
            _log.Debug("DELETED: {path}", e.FullPath);
            var directory = Path.GetDirectoryName(e.FullPath);
            if (directory != null)
            {
                if (_definitions.TryRemove(directory, out _))
                {
                    DefinitionFilesChanged?.Invoke(Enumerable.Empty<string>(), Enumerable.Empty<string>(), new string[] { directory });
                }
            }
        }
    }

    private void OnDefinitionFileCreated(object sender, FileSystemEventArgs e)
    {
        if (Path.GetFileName(e.Name) == CustomizeDefinition.Filename && Path.GetDirectoryName(e.FullPath) is string newDirectory && TryLoadDefinition(e.FullPath, out var definition))
        {
            _log.Debug("NEW: {path}", e.FullPath);
            _definitions[newDirectory] = definition;
            DefinitionFilesChanged?.Invoke(new string[] { newDirectory }, Enumerable.Empty<string>(), Enumerable.Empty<string>());
        }
    }

    private void OnDefinitionFileChanged(object sender, FileSystemEventArgs e)
    {
        if (Path.GetFileName(e.Name) == CustomizeDefinition.Filename && Path.GetDirectoryName(e.FullPath) is string directory)
        {
            _log.Debug("CHANGE: {path}", e.FullPath);
            if (TryLoadDefinition(e.FullPath, out var definition))
            {
                bool updated = false;
                _definitions.AddOrUpdate(directory, definition, (key, existing) =>
                {
                    updated = true;
                    return definition;
                });

                if (updated)
                {
                    DefinitionFilesChanged?.Invoke(Enumerable.Empty<string>(), new string[] { directory }, Enumerable.Empty<string>());
                }
                else
                {
                    DefinitionFilesChanged?.Invoke(new string[] { directory }, Enumerable.Empty<string>(), Enumerable.Empty<string>());
                }
            }
            else if (_definitions.TryRemove(directory, out _))
            {
                DefinitionFilesChanged?.Invoke(Enumerable.Empty<string>(), Enumerable.Empty<string>(), new string[] { directory });
            }
        }
    }

    private int _scanning = 0;
    private void RescanDefinitionFiles(string penumbraModDirectory)
    {
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) == 0)
        {
            // We need to exchange the old dictionary for the new one at the start, otherwise filesystem events might go to the old dictionary
            // after we start but before we swap in the new dictionary.
            var newDictionary = new ConcurrentDictionary<string, CustomizeDefinition>();
            var oldDictionary = Interlocked.Exchange(ref _definitions, newDictionary);
            Task.Run(() =>
            {
                try
                {
                    List<string> updatedPaths = new();
                    List<string> newPaths = new();
                    foreach (var modDirectory in Directory.EnumerateDirectories(penumbraModDirectory))
                    {
                        var definitionPath = Path.Combine(modDirectory, CustomizeDefinition.Filename);
                        if (File.Exists(definitionPath))
                        {
                            if (TryLoadDefinition(definitionPath, out var definition))
                            {
                                newDictionary[modDirectory] = definition;
                                // If this was present in the previous dictionary, let's assume it's modified. Otherwise, it's new.
                                if (oldDictionary.TryRemove(modDirectory, out _))
                                {
                                    updatedPaths.Add(modDirectory);
                                }
                                else
                                {
                                    newPaths.Add(modDirectory);
                                }
                            }
                        }
                    }

                    if (oldDictionary.Count > 0 || updatedPaths.Count > 0 ||  newPaths.Count > 0)
                    {
                        DefinitionFilesChanged?.Invoke(newPaths, updatedPaths, oldDictionary.Keys);
                    }
                }
                finally
                {
                    _scanning = 0;
                }
            });
        }
    }

    private bool TryLoadDefinition(string path, [NotNullWhen(true)] out CustomizeDefinition? definition)
    {
        Exception? lastException = null;

        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    definition = CustomizeDefinition.FromStream(stream);
                    return definition != null;
                }
            }
            catch (IOException ioException)
            {
                // Oftentimes when we react to the FileSystemWatcher the file is actually still being written to.
                // Wait a little and see if the file becomes available.
                lastException = ioException;
                Thread.Sleep(10);
                continue;
            }
            catch (Exception exception)
            {
                // General exceptions aren't worth retrying
                lastException = exception;
                break;
            }
        }

        _log.Warning(lastException, "Failed to open {path}.", path);
        definition = null;
        return false;
    }

    public bool TryGetModCustomizeDefinition(string modDirectory, [NotNullWhen(true)] out CustomizeDefinition? definition)
    {
        return _definitions.TryGetValue(modDirectory, out definition);
    }

    public void Dispose()
    {
        _fileSystemWatcher.EnableRaisingEvents = false;
        _fileSystemWatcher.Dispose();
    }
}
