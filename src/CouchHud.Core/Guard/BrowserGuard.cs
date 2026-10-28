namespace CouchHud.Core.Guard;

/// <summary>
/// The couch browser must not lead out of the couch: no Windows file or print dialogs from web pages and no
/// download windows. Pure rules and the script; the WebView2 wiring lives in the Shell project.
/// </summary>
public static class BrowserGuard
{
    /// <summary>The web APIs (and DOM calls) a page can use to make Windows show a file picker or a print dialog.</summary>
    public static readonly IReadOnlyList<string> NeutralizedApis =
        ["print", "showOpenFilePicker", "showSaveFilePicker", "showDirectoryPicker", "input[type=file]", "input[type=color]",
         "EyeDropper", "PaymentRequest", "share", "credentials", "requestPictureInPicture", "documentPictureInPicture"];

    /// <summary>
    /// Command line of the page's browser: no picture-in-picture of any kind. A PiP window floats above every window, also above the HUD
    /// and its full-screen strip, stays after the guide button, and Document PiP even takes key presses: a page could show a fake code
    /// prompt in it. (One --disable-features only: Chromium takes the last one of a switch.)
    /// </summary>
    public const string BrowserArguments =
        "--disable-blink-features=PictureInPictureAPI,DocumentPictureInPictureAPI " +
        "--disable-features=DocumentPictureInPictureAPI,MediaSessionEnterPictureInPicture";

    /// <summary>Downloads never get the browser's own UI. Unlocked they are asked about in the HUD (then into Downloads), locked they are cancelled.</summary>
    public static (bool Handled, bool Cancel) Download(bool allowed) => (true, !allowed);

    /// <summary>Runs in every document and frame before the page's own scripts.</summary>
    public const string Script = """
        (() => {
          const refuse = () => Promise.reject(new DOMException('Not allowed', 'NotAllowedError'));
          const lock = (name, value) => {
            try { Object.defineProperty(window, name, { value, configurable: false, writable: false }); } catch { /* already fixed */ }
          };
          lock('showOpenFilePicker', refuse);
          lock('showSaveFilePicker', refuse);
          lock('showDirectoryPicker', refuse);
          lock('print', () => {});
          // The eye dropper, payment sheets, the Windows share window and the passkey or password windows (WebAuthn, credential
          // manager) are windows of their own as well; none of them has a place on the couch.
          lock('EyeDropper', undefined);
          lock('PaymentRequest', undefined);
          const nav = (name, value) => {
            try { Object.defineProperty(Navigator.prototype, name, { value, configurable: false, writable: false }); } catch { /* already fixed */ }
          };
          nav('share', refuse);
          nav('canShare', () => false);
          nav('credentials', Object.freeze({ get: refuse, create: refuse, store: refuse, preventSilentAccess: () => Promise.resolve() }));
          if (window.PublicKeyCredential) lock('PublicKeyCredential', undefined);
          // Picture-in-picture floats above every window, the HUD and its full-screen strip too (Document PiP even takes keys): never.
          // (Also switched off on the browser's command line; this is the second lock.)
          lock('documentPictureInPicture', undefined);
          try {
            const video = HTMLVideoElement.prototype;
            Object.defineProperty(video, 'requestPictureInPicture', { value: refuse, configurable: false, writable: false });
            Object.defineProperty(video, 'disablePictureInPicture', { get: () => true, set: () => {}, configurable: false });
          } catch { /* already fixed */ }
          try { Object.defineProperty(Document.prototype, 'pictureInPictureEnabled', { get: () => false, configurable: false }); } catch { /* already fixed */ }
          // A file input opens the Windows file dialog, a color input the Windows color dialog: both are windows of their own
          // that take the controller away from the couch.
          const isFile = (e) => e instanceof HTMLInputElement && (e.type === 'file' || e.type === 'color');
          // A click on a file input, or on the label of one, would open the Windows file dialog.
          document.addEventListener('click', (e) => {
            const t = e.target;
            const label = t && t.closest ? t.closest('label') : null;
            if (isFile(t) || (label && isFile(label.control))) {
              e.preventDefault();
              e.stopImmediatePropagation();
            }
          }, true);
          const click = HTMLInputElement.prototype.click;
          HTMLInputElement.prototype.click = function () { return isFile(this) ? undefined : click.call(this); };
          const picker = HTMLInputElement.prototype.showPicker;
          if (picker) HTMLInputElement.prototype.showPicker = function () { return isFile(this) ? undefined : picker.call(this); };
        })();
        """;
}
