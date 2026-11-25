using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Kl.Client;
using Kl.Gui.Models;
using Kl.Gui.Services;

namespace Kl.Gui.ViewModels;

/// <summary>A follow-up prompt waiting for the current turn to finish.</summary>
public sealed record QueuedTurn(string Prompt, IReadOnlyList<AttachmentItemViewModel> Attachments);
