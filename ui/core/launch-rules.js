// Startup options of an entry: which rows the edit menu shows. Pure, tested in scripts/ui-logic-check.mjs; the host checks everything again.

export const TARGET_EXTENSIONS = ['.exe', '.bat', '.cmd'];

/** A batch file: cmd.exe reads its parameters with its own rules. */
export const isScript = (path) => /\.(bat|cmd)$/i.test(String(path ?? ''));

/**
 * What the host refuses in a batch file's parameters (LaunchOptions.CheckScriptArguments): what cmd.exe would read as a command, and anything
 * but plain ASCII (Windows' "best fit" turns lookalikes such as a full-width ＆ into the real one).
 */
export const scriptArgsProblem = (text, target) => isScript(target) && /[&|^<>()%!]|[^\x20-\x7E]/.test(String(text ?? ''));

/**
 * The rows of the "Startup options" menu for an entry, from the host's view (getLaunchOptions). Expert mode only (the simple mode keeps no
 * technical rows, and has no second switch for it); null when there is nothing to offer.
 * args: 'edit' (own fields or override), 'steam' / 'launcher' (an honest note where they are set), null (none).
 */
export function launchRows(view, { expert = false } = {}) {
  if (!expert || !view) return null;
  const mode = view.argsMode;
  const args = mode === 'own' || mode === 'override' ? 'edit' : mode === 'steam' || mode === 'launcher' ? mode : null;
  const rows = {
    args,
    cwd: args === 'edit',
    target: !!view.targetAllowed,
    reset: !!view.custom
  };
  return rows.args || rows.target || rows.reset ? rows : null;
}

/** The row's second line for the start target: the file name (folder stays in the dialog) or "automatic". */
export const targetLabel = (view, automatic) => (view?.target ? String(view.target).split(/[\\/]/).pop() : automatic);
