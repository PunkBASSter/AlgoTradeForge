using Microsoft.Playwright;

namespace AlgoTradeForge.WebApi.PlaywrightTests.Components;

/// <summary>
/// Wraps CodeMirror 6 editor interaction. Used by both DashboardPage (RunNewPanel) and DebugPage.
/// </summary>
public sealed class CodeMirrorEditor(IPage page)
{
    public async Task WaitForReadyAsync(int timeout = 10_000)
    {
        await page.GetByTestId("json-editor").WaitForAsync(new() { Timeout = timeout });
    }

    public async Task SetContentAsync(string json)
    {
        await page.EvaluateAsync(@"(newContent) => {
            const content = document.querySelector('.cm-content');
            if (!content) throw new Error('CodeMirror content element not found');
            const view = content.cmTile?.view;
            if (!view) throw new Error('CodeMirror EditorView not found on .cm-content.cmTile');
            view.dispatch({
                changes: { from: 0, to: view.state.doc.length, insert: newContent }
            });
        }", json);
    }
}
