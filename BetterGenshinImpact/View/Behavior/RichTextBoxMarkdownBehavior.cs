using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using BetterGenshinImpact.Helpers;

namespace BetterGenshinImpact.View.Behavior;

public static class RichTextBoxMarkdownBehavior
{
    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.RegisterAttached(
            "Markdown",
            typeof(string),
            typeof(RichTextBoxMarkdownBehavior),
            new PropertyMetadata(string.Empty, OnMarkdownChanged));

    private static readonly DependencyProperty MarkdownStateProperty =
        DependencyProperty.RegisterAttached(
            "MarkdownState",
            typeof(MarkdownState),
            typeof(RichTextBoxMarkdownBehavior),
            new PropertyMetadata(null));

    private static readonly TimeSpan MarkdownDebounceDelay = TimeSpan.FromMilliseconds(180);

    public static string GetMarkdown(DependencyObject obj)
    {
        return (string)obj.GetValue(MarkdownProperty);
    }

    public static void SetMarkdown(DependencyObject obj, string value)
    {
        obj.SetValue(MarkdownProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox richTextBox)
        {
            return;
        }

        var state = GetOrCreateState(richTextBox);
        state.PendingMarkdown = e.NewValue as string ?? string.Empty;
        if (string.IsNullOrWhiteSpace(state.PendingMarkdown))
        {
            state.Timer.Stop();
            ApplyMarkdown(richTextBox, string.Empty);
            return;
        }

        state.Timer.Stop();
        state.Timer.Start();
    }

    private static MarkdownState GetOrCreateState(RichTextBox richTextBox)
    {
        if (richTextBox.GetValue(MarkdownStateProperty) is MarkdownState existing)
        {
            return existing;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Background, richTextBox.Dispatcher)
        {
            Interval = MarkdownDebounceDelay
        };
        var state = new MarkdownState(timer, new WeakReference<RichTextBox>(richTextBox));
        timer.Tag = state;
        timer.Tick += OnMarkdownDebounceTick;

        richTextBox.Unloaded += OnRichTextBoxUnloaded;
        richTextBox.SetValue(MarkdownStateProperty, state);
        return state;
    }

    private static void OnRichTextBoxUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not RichTextBox richTextBox)
        {
            return;
        }

        if (richTextBox.GetValue(MarkdownStateProperty) is MarkdownState state)
        {
            state.Timer.Stop();
        }
    }

    private static void OnMarkdownDebounceTick(object? sender, EventArgs e)
    {
        if (sender is not DispatcherTimer timer || timer.Tag is not MarkdownState state)
        {
            return;
        }

        timer.Stop();
        if (!state.Owner.TryGetTarget(out var richTextBox))
        {
            return;
        }

        ApplyMarkdown(richTextBox, state.PendingMarkdown);
    }

    private static void ApplyMarkdown(RichTextBox richTextBox, string markdown)
    {
        try
        {
            var document = LooksLikeMarkdown(markdown)
                ? MarkdownToFlowDocumentConverter.ConvertToFlowDocument(markdown)
                : BuildPlainTextDocument(markdown);
            richTextBox.Document = document;
        }
        catch
        {
            richTextBox.Document = BuildPlainTextDocument(markdown);
        }
    }

    private static bool LooksLikeMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("```", StringComparison.Ordinal) ||
               text.Contains("**", StringComparison.Ordinal) ||
               text.Contains("__", StringComparison.Ordinal) ||
               text.Contains("`", StringComparison.Ordinal) ||
               text.Contains("# ", StringComparison.Ordinal) ||
               text.Contains("- ", StringComparison.Ordinal) ||
               text.Contains("* ", StringComparison.Ordinal) ||
               text.Contains("> ", StringComparison.Ordinal) ||
               (text.Contains('[', StringComparison.Ordinal) && text.Contains("](", StringComparison.Ordinal));
    }

    private static FlowDocument BuildPlainTextDocument(string text)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity
        };

        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var normalized = text?.Replace("\r\n", "\n", StringComparison.Ordinal) ?? string.Empty;
        var lines = normalized.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            paragraph.Inlines.Add(new Run(lines[i]));
        }

        doc.Blocks.Add(paragraph);
        return doc;
    }

    private sealed class MarkdownState
    {
        public MarkdownState(DispatcherTimer timer, WeakReference<RichTextBox> owner)
        {
            Timer = timer;
            Owner = owner;
        }

        public DispatcherTimer Timer { get; }

        public WeakReference<RichTextBox> Owner { get; }

        public string PendingMarkdown { get; set; } = string.Empty;
    }
}
