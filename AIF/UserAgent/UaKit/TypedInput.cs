using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Mpai.UaKit;

// THE TEXT THE PERSON TYPES. A device for a Text Object (OSD-BTO): the text box opens when the workflow asks, and Enter - the key or
// the button - gives what was typed. The step ends with the words, or with nothing when the person speaks instead (the step is
// abandoned) or Stop is pressed; the box is closed again either way. The window supplies the controls; it does not read them.
public sealed class TypedInput
{
    private readonly Dispatcher _ui;
    private readonly TextBox _box;
    private readonly Button? _send;
    private TaskCompletionSource<string?>? _waiting;

    public TypedInput(TextBox box, Button? send = null)
    {
        _box = box; _send = send; _ui = box.Dispatcher;
        _box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Submit(); };
        if (_send is not null) _send.Click += (_, _) => Submit();
        Close();
    }

    // The words typed; null when abandoned or stopped.
    public async Task<string?> AskAsync(CancellationToken stop, CancellationToken abandon)
    {
        var waiting = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting = waiting;
        await _ui.InvokeAsync(() =>
        {
            _box.IsEnabled = true; if (_send is not null) _send.IsEnabled = true;
            _box.Clear(); _box.Focus();
        });
        using var onStop    = stop.Register(() => waiting.TrySetResult(null));
        using var onAbandon = abandon.Register(() => waiting.TrySetResult(null));
        var words = await waiting.Task;
        if (ReferenceEquals(_waiting, waiting)) _waiting = null;
        await _ui.InvokeAsync(Close);
        return string.IsNullOrWhiteSpace(words) ? null : words.Trim();
    }

    private void Submit()
    {
        var waiting = _waiting;
        if (waiting is null || string.IsNullOrWhiteSpace(_box.Text)) return;   // Enter on an empty box gives nothing
        waiting.TrySetResult(_box.Text);
    }

    private void Close()
    {
        _box.IsEnabled = false; if (_send is not null) _send.IsEnabled = false;
        _box.Clear();
    }
}
