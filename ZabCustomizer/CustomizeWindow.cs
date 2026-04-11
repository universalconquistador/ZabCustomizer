using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZabCustomizer;

public class CustomizeWindow : Window
{
    private CustomizeDefinition _definition;
    private readonly ITextureProvider _textureProvider;
    private readonly DefinitionManager _definitionManager;
    private readonly TextureCompressor _textureCompressor;
    private readonly Config _config;

    private readonly FileDialogManager _fileDialogManager;
    private readonly Dictionary<string, string> _groupNames = new();

    private object _statusLock = new();
    private readonly List<string> _validationErrors = new();
    private bool _isReady = false;

    /// <summary>
    /// The directory of the Penumbra mod that this window is for customizing.
    /// </summary>
    public string ModDirectory { get; }

    public event Action? Closed;
    public event Action? ModChanged;

    private int _selectedSlotIndex = 0;
    private string _inputTextureFilename = "";
    private string _inputOptionName = "New Option";
    private readonly HashSet<string> _selectedOutputGroups = new();

    public CustomizeWindow(string modDirectory, CustomizeDefinition definition, ITextureProvider textureProvider, DefinitionManager definitionManager, TextureCompressor textureCompressor, Config config)
        : base($"Customize {Path.GetFileName(modDirectory)}")
    {
        ModDirectory = modDirectory;
        _definition = definition;
        _textureProvider = textureProvider;
        _definitionManager = definitionManager;
        _textureCompressor = textureCompressor;
        _config = config;

        _definitionManager.DefinitionFilesChanged += this.OnDefinitionFilesChanged;

        _fileDialogManager = new();

        SizeConstraints = new()
        {
            MinimumSize = new(800, 400),
        };

        RefreshGroupNames();
    }

    private void OnDefinitionFilesChanged(IEnumerable<string> addedDefinitionFiles, IEnumerable<string> modifiedDefinitionFiles, IEnumerable<string> removedDefinitionFiles)
    {
        if (modifiedDefinitionFiles.Contains(ModDirectory))
        {
            if (_definitionManager.TryGetModCustomizeDefinition(ModDirectory, out var newDefinition))
            {
                _definition = newDefinition;
                _ = RefreshStatus(_inputTextureFilename, _inputOptionName);
                RefreshGroupNames();
            }
            else
            {
                // New json is invalid; close window
                //IsOpen = false;
            }
        }
        else if (removedDefinitionFiles.Contains(ModDirectory))
        {
            IsOpen = false;
        }
    }

    public override void Draw()
    {
        if (_definition.Notes.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(_definition.Notes);
            ImGui.Spacing();
        }

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.Combo("###SlotCombo", ref _selectedSlotIndex, _definition.Slots, slot => slot.DisplayName))
        {
            _ = RefreshStatus(_inputTextureFilename, _inputOptionName);
        }
        ImGuiHelpers.ScaledDummy(2.0f);

        using (ImRaii.PushIndent())
        {
            if (_selectedSlotIndex >= 0 && _selectedSlotIndex < _definition.Slots.Count)
            {
                if (_definition.Slots[_selectedSlotIndex].Notes.Length > 0)
                {
                    ImGui.Spacing();
                    ImGui.TextWrapped(_definition.Slots[_selectedSlotIndex].Notes);
                    ImGui.Spacing();
                }
            }

            using (var table = ImRaii.Table("###SlotTable", numColumns: 2))
            {
                ImGui.TableSetupColumn("Properties", ImGuiTableColumnFlags.WidthStretch, 1.0f);
                ImGui.TableSetupColumn("TexturePreview", ImGuiTableColumnFlags.WidthFixed, 250.0f * ImGuiHelpers.GlobalScale);

                ImGui.TableNextColumn();

                // Input properties
                var itemWidth = ImGui.GetContentRegionAvail().X * 2.0f / 3.0f;
                using (ImRaii.ItemWidth(itemWidth))
                {
                    ImGui.SetNextItemWidth(itemWidth - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemInnerSpacing.X);
                    if (ImGui.InputTextWithHint("##TextureFile", "My Texture.png", ref _inputTextureFilename))
                    {
                        _ = RefreshStatus(_inputTextureFilename, _inputOptionName);
                    }
                    ImGui.SameLine(0.0f, ImGui.GetStyle().ItemInnerSpacing.X);
                    if (ImGuiComponents.IconButton(FontAwesomeIcon.Folder, new(ImGui.GetFrameHeight())))
                    {
                        _fileDialogManager.OpenFileDialog("Select Input Texture", ".png", (success, paths) =>
                        {
                            if (success && paths.Count == 1 && paths[0] is string path)
                            {
                                _inputTextureFilename = path;
                                _inputOptionName = Path.GetFileNameWithoutExtension(path);
                                _config.LastBrowseDirectory = Path.GetDirectoryName(path) ?? ".";
                                _config.Save();
                                _ = RefreshStatus(_inputTextureFilename, _inputOptionName);
                            }
                        }, selectionCountMax: 1, startPath: _config.LastBrowseDirectory);
                    }
                    ImGui.SameLine(0.0f, ImGui.GetStyle().ItemInnerSpacing.X);
                    ImGui.TextUnformatted("Texture File");

                    if (ImGui.InputTextWithHint("New Option Name", "My Option", ref _inputOptionName))
                    {
                        _ = RefreshStatus(_inputTextureFilename, _inputOptionName);
                    }
                }

                // Destination checkboxes
                ImGui.Spacing();
                ImGui.Text("Add to Groups:");
                foreach (var destination in _definition.Slots[_selectedSlotIndex].Destinations)
                {
                    bool isChecked = _selectedOutputGroups.Contains(destination.GroupJsonFilename);

                    string groupLabel = destination.GroupJsonFilename;
                    if (_groupNames.TryGetValue(destination.GroupJsonFilename, out var groupName))
                    {
                        groupLabel = groupName;
                    }
                    if (ImGui.Checkbox(groupLabel, ref isChecked))
                    {
                        if (isChecked)
                        {
                            _selectedOutputGroups.Add(destination.GroupJsonFilename);
                            _ = RefreshStatus(_inputTextureFilename, _inputOptionName);
                        }
                        else
                        {
                            _selectedOutputGroups.Remove(destination.GroupJsonFilename);
                            _ = RefreshStatus(_inputTextureFilename, _inputOptionName);
                        }
                    }
                }

                // Error messages
                ImGui.Spacing();
                lock (_statusLock)
                {
                    using (ImRaii.PushIndent(4.0f))
                    using (ImRaii.TextWrapPos(ImGui.GetContentRegionAvail().X))
                    {
                        foreach (var message in _validationErrors)
                        {
                            using (ImRaii.PushFont(UiBuilder.IconFont))
                            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudRed))
                            {
                                ImGui.Text(FontAwesomeIcon.ExclamationCircle.ToIconString());
                            }
                            ImGui.SameLine();
                            ImGui.TextWrapped(message);
                            ImGuiHelpers.ScaledDummy(1.0f);
                        }
                    }
                }

                // Image preview
                ImGui.TableNextColumn();
                var width = ImGui.GetContentRegionAvail().X;
                var filePreviewTexture = _textureProvider.GetFromFileAbsolute(_inputTextureFilename);
                if (filePreviewTexture.TryGetWrap(out var wrap, out var exception))
                {
                    var hscale = width / wrap.Width;
                    var vscale = width / wrap.Height;
                    var scale = MathF.Min(hscale, vscale);

                    var cursor = ImGui.GetCursorPos();
                    ImGui.SetCursorPos(cursor + new Vector2(width / 2.0f - wrap.Width * scale / 2.0f, width / 2.0f - wrap.Height * scale / 2.0f));
                    ImGui.Image(wrap.Handle, new(wrap.Width * scale, wrap.Height * scale));

                    ImGui.SetCursorPos(cursor + new Vector2(0.0f, width + ImGui.GetStyle().ItemSpacing.Y));
                    var infoText = $"{wrap.Width} x {wrap.Height} pixels";
                    var infoTextWidth = ImGui.CalcTextSize(infoText).X;
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X / 2.0f - infoTextWidth / 2.0f);
                    ImGui.TextDisabled(infoText);
                }
                else
                {
                    ImGui.AddRect(ImGui.GetWindowDrawList(), ImGui.GetCursorScreenPos(), ImGui.GetCursorScreenPos() + new Vector2(width, width), ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.9f, 0.9f, 0.1f)));
                    ImGuiHelpers.ScaledDummy(width);
                }
            }

            if (ImGui.GetCursorPosY() < ImGui.GetContentRegionMax().Y - ImGui.GetFrameHeight())
            {
                ImGui.SetCursorPosY(ImGui.GetContentRegionMax().Y - ImGui.GetFrameHeight());
            }

            using (ImRaii.Disabled(!_isReady || _isAdding == 1))
            {
                bool anyErrors = _validationErrors.Count > 0;
                if (ImGuiComponents.IconButtonWithText(_isAdding == 1 ? FontAwesomeIcon.Spinner :( anyErrors ? FontAwesomeIcon.ExclamationCircle : FontAwesomeIcon.Check), _isAdding == 1 ? _addStatus : (anyErrors ? $"{_validationErrors.Count} {(_validationErrors.Count > 1 ? "Issues" : "Issue")}" : $"Add {_inputOptionName} to {Path.GetFileName(ModDirectory)}"), new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight())))
                {
                    _ = AddToSlot(_definition.Slots[_selectedSlotIndex], _inputTextureFilename, _inputOptionName);
                }
            }
        }

        _fileDialogManager.Draw();
    }

    private void RefreshGroupNames()
    {
        _groupNames.Clear();
        foreach (var groupName in PenumbraModUtils.GetGroups(ModDirectory))
        {
            _groupNames[groupName.jsonName] = groupName.groupName;
        }
    }

    // sets _validationErrors and _isReady
    private int _isRefreshing = 0;
    private Task RefreshStatus(string imageFilename, string optionName)
    {
        if (_selectedSlotIndex < 0 || _selectedSlotIndex >= _definition.Slots.Count)
        {
            // The selected slot index is not valid
            _selectedSlotIndex = _definition.Slots.Count > 0 ? 0 : -1;
            lock (_statusLock)
            {
                _validationErrors.Clear();
                _isReady = false;
            }
            return Task.CompletedTask;
        }

        if (!_definition.Slots[_selectedSlotIndex].Destinations.Any(destination => _selectedOutputGroups.Contains(destination.GroupJsonFilename)))
        {
            // None of the destinations are selected
            lock (_statusLock)
            {
                _validationErrors.Clear();
                _isReady = false;
            }
            return Task.CompletedTask;
        }

        if (Interlocked.Exchange(ref _isRefreshing, 1) == 0)
        {
            var tex = _textureProvider.GetFromFileAbsolute(imageFilename);
            var slot = _definition.Slots[_selectedSlotIndex];
            return Task.Run(async () =>
            {
                try
                {
                    var outputFilename = $"{SanitizeDisplayNameToFileName(optionName)}.tex";
                    var outputPath = Path.Combine(ModDirectory, slot.OutputDirectory, outputFilename);
                    if (imageFilename == string.Empty)
                    {
                        lock (_statusLock)
                        {
                            _validationErrors.Clear();
                            _isReady = false;
                        }
                        return;
                    }

                    if (File.Exists(outputPath))
                    {
                        lock (_statusLock)
                        {
                            _validationErrors.Clear();
                            _validationErrors.Add($"File {Path.Combine(slot.OutputDirectory, outputFilename)} already exists. Choose a different name.");
                            _isReady = false;
                        }
                        return;
                    }

                    using (var wrap = await tex.RentAsync())
                    {
                        lock (_statusLock)
                        {
                            _validationErrors.Clear();
                            _isReady = true;

                            if (wrap.Width % 4 != 0)
                            {
                                _validationErrors.Add($"The width of the input image must be a multiple of 4. Current width of '{Path.GetFileName(imageFilename)}': {wrap.Width} px");
                                _isReady = false;
                            }

                            if (wrap.Height % 4 != 0)
                            {
                                _validationErrors.Add($"The height of the input image must be a multiple of 4. Current height of '{Path.GetFileName(imageFilename)}': {wrap.Height} px");
                                _isReady = false;
                            }

                            if (!MatchesAspectRatio(wrap.Width, wrap.Height, slot.AspectRecommendationWidth, slot.AspectRecommendationHeight))
                            {
                                _validationErrors.Add($"The aspect ratio of the input image must be {slot.AspectRecommendationWidth}:{slot.AspectRecommendationHeight}. Current aspect ratio: {slot.AspectRecommendationWidth}:{(float)wrap.Height / wrap.Width * slot.AspectRecommendationWidth:F2}.");
                                _isReady = false;
                            }
                        }
                    }
                }
                catch (AggregateException ag) when (ag.InnerException is FileNotFoundException)
                {
                    lock (_statusLock)
                    {
                        _validationErrors.Clear();
                        _validationErrors.Add("Could not find file.");
                        _isReady = false;
                    }
                }
                catch (Exception ex)
                {
                    lock (_statusLock)
                    {
                        _validationErrors.Clear();
                        _validationErrors.Add("Failed to load file: " + ex.ToString());
                        _isReady = false;
                    }
                }
                finally
                {
                    _isRefreshing = 0;
                }
            });
        }
        else
        {
            return Task.CompletedTask;
        }
    }

    private int _isAdding = 0;
    private string _addStatus = "";
    private Task AddToSlot(CustomizeSlot slot, string inputImageFilename, string inputOptionName)
    {
        _addStatus = "Compressing texture...";
        if (Interlocked.Exchange(ref _isAdding, 1) == 0)
        {
            return Task.Run(async () =>
            {
                try
                {
                    // Compress the input PNG to a BC7 .tex file
                    var outputFilename = $"{SanitizeDisplayNameToFileName(inputOptionName)}.tex";
                    var outputPath = Path.Combine(ModDirectory, slot.OutputDirectory, outputFilename);

                    Directory.CreateDirectory(Path.Combine(ModDirectory, slot.OutputDirectory));
                    _textureCompressor.CompressToTexFile(inputImageFilename, outputPath);

                    // Add the options to the Penumbra mod group JSONs
                    foreach (var destination in slot.Destinations)
                    {
                        if (_selectedOutputGroups.Contains(destination.GroupJsonFilename))
                        {
                            _addStatus = $"Adding option to ${Path.GetFileNameWithoutExtension(destination.GroupJsonFilename)}";
                            await PenumbraModUtils.AddGroupOptionAsync(Path.Combine(ModDirectory, destination.GroupJsonFilename), inputOptionName, new()
                            {
                                { destination.GamePath, Path.Combine(slot.OutputDirectory, outputFilename) },
                            });
                        }
                    }

                    // Let Penumbra know to reload the mod
                    _addStatus = "Reloading mod...";
                    ModChanged?.Invoke();

                    _addStatus = "Done!";
                }
                finally
                {
                    _isAdding = 0;
                }
            });
        }
        else
        {
            return Task.CompletedTask;
        }
    }

    public override void OnClose()
    {
        base.OnClose();

        Closed?.Invoke();
    }

    // Verifies an aspect ratio between two fractions, without any floating-point precision issues
    private static bool MatchesAspectRatio(int numerator1, int denominator1, int numerator2, int denominator2)
    {
        return (ulong)numerator1 * (ulong)denominator2 == (ulong)numerator2 * (ulong)denominator1;
    }

    private static string SanitizeDisplayNameToFileName(string displayName)
    {
        return displayName.Replace('\'', '_').Replace(':', '_').Replace('$', '_').Replace('@', '_').Replace('#', '_').Replace('+', '_').Replace('?', '_').Replace(' ', '_').Replace('\\', '_').Replace('/', '_');
    }
}
