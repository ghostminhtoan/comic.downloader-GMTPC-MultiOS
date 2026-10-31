using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ComicDownloaderGMTPC.ViewModels;

public class SearchState
{
    public string Positive { get; set; } = string.Empty;
    public string NonNegative { get; set; } = string.Empty;
    public string NonPositive { get; set; } = string.Empty;
    public string Negative { get; set; } = string.Empty;
    public bool IsExactWord { get; set; } = false;
}

public partial class AdvancedSearchViewModel : ObservableObject
{
    private readonly Action? _applyCallback;
    private readonly DispatcherTimer _debounceTimer;
    private readonly Stack<SearchState> _undoStack = new();
    private readonly Stack<SearchState> _redoStack = new();
    private SearchState? _pendingStateForUndo;
    private bool _isRestoringState;

    [ObservableProperty]
    private string _positiveText = string.Empty;

    [ObservableProperty]
    private string _nonNegativeText = string.Empty;

    [ObservableProperty]
    private string _nonPositiveText = string.Empty;

    [ObservableProperty]
    private string _negativeText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContainsMode))]
    [NotifyPropertyChangedFor(nameof(FilterIconBrush))]
    private bool _isExactWord;

    public bool IsContainsMode
    {
        get => !IsExactWord;
        set
        {
            if (value)
            {
                IsExactWord = false;
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterIconBrush))]
    private bool _isActive;

    [ObservableProperty]
    private bool _canUndo;

    [ObservableProperty]
    private bool _canRedo;

    public string FilterIconBrush => IsActive ? "#10B981" : "#94A3B8";

    public AdvancedSearchViewModel(Action? applyCallback = null)
    {
        _applyCallback = applyCallback;

        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _debounceTimer.Tick += DebounceTimer_Tick;
    }

    partial void OnPositiveTextChanged(string value) => OnTextChanged();
    partial void OnNonNegativeTextChanged(string value) => OnTextChanged();
    partial void OnNonPositiveTextChanged(string value) => OnTextChanged();
    partial void OnNegativeTextChanged(string value) => OnTextChanged();
    partial void OnIsExactWordChanged(bool value) => OnTextChanged();

    private void OnTextChanged()
    {
        if (_isRestoringState) return;

        ApplySearch();

        _pendingStateForUndo ??= CreateSnapshot();

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void DebounceTimer_Tick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        if (_pendingStateForUndo != null && !_isRestoringState)
        {
            _undoStack.Push(_pendingStateForUndo);
            _redoStack.Clear();
            _pendingStateForUndo = null;
            UpdateUndoRedoState();
        }
    }

    private SearchState CreateSnapshot()
    {
        return new SearchState
        {
            Positive = PositiveText ?? string.Empty,
            NonNegative = NonNegativeText ?? string.Empty,
            NonPositive = NonPositiveText ?? string.Empty,
            Negative = NegativeText ?? string.Empty,
            IsExactWord = IsExactWord
        };
    }

    private void PushUndoState()
    {
        _undoStack.Push(CreateSnapshot());
        _redoStack.Clear();
        _pendingStateForUndo = null;
        UpdateUndoRedoState();
    }

    private void RestoreSnapshot(SearchState state)
    {
        _isRestoringState = true;
        try
        {
            PositiveText = state.Positive;
            NonNegativeText = state.NonNegative;
            NonPositiveText = state.NonPositive;
            NegativeText = state.Negative;
            IsExactWord = state.IsExactWord;

            var pos = GetActivePositiveKeywords();
            var nonNeg = GetActiveNonNegativeKeywords();
            var nonPos = GetActiveNonPositiveKeywords();
            var neg = GetActiveNegativeKeywords();
            IsActive = pos.Count > 0 || nonNeg.Count > 0 || nonPos.Count > 0 || neg.Count > 0 || IsExactWord;

            _applyCallback?.Invoke();
        }
        finally
        {
            _isRestoringState = false;
        }
        UpdateUndoRedoState();
    }

    [RelayCommand]
    public void Clear()
    {
        PushUndoState();
        _isRestoringState = true;
        try
        {
            PositiveText = string.Empty;
            NonNegativeText = string.Empty;
            NonPositiveText = string.Empty;
            NegativeText = string.Empty;
            IsExactWord = false;
            IsActive = false;
            _applyCallback?.Invoke();
        }
        finally
        {
            _isRestoringState = false;
        }
        UpdateUndoRedoState();
    }

    [RelayCommand]
    public void Undo()
    {
        if (_undoStack.Count > 0)
        {
            var currentState = CreateSnapshot();
            _redoStack.Push(currentState);
            var previousState = _undoStack.Pop();
            RestoreSnapshot(previousState);
        }
    }

    [RelayCommand]
    public void Redo()
    {
        if (_redoStack.Count > 0)
        {
            var currentState = CreateSnapshot();
            _undoStack.Push(currentState);
            var nextState = _redoStack.Pop();
            RestoreSnapshot(nextState);
        }
    }

    private void UpdateUndoRedoState()
    {
        CanUndo = _undoStack.Count > 0;
        CanRedo = _redoStack.Count > 0;
    }

    public void ApplySearch()
    {
        var pos = GetActivePositiveKeywords();
        var nonNeg = GetActiveNonNegativeKeywords();
        var nonPos = GetActiveNonPositiveKeywords();
        var neg = GetActiveNegativeKeywords();
        IsActive = pos.Count > 0 || nonNeg.Count > 0 || nonPos.Count > 0 || neg.Count > 0 || IsExactWord;

        _applyCallback?.Invoke();
    }

    private static List<string> ParseKeywords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return text.Split(';')
                   .Select(k => k.Trim())
                   .Where(k => !string.IsNullOrEmpty(k))
                   .ToList();
    }

    public List<string> GetActivePositiveKeywords() => ParseKeywords(PositiveText);
    public List<string> GetActiveNonNegativeKeywords() => ParseKeywords(NonNegativeText);
    public List<string> GetActiveNonPositiveKeywords() => ParseKeywords(NonPositiveText);
    public List<string> GetActiveNegativeKeywords() => ParseKeywords(NegativeText);
}

