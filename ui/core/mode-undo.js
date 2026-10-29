// What a step of the mode switch records before it runs and how it is undone (see core/mode-transaction.js). A factory so the real tables can
// be run against a fake host in scripts/ui-logic-check.mjs: `api` is the host bridge, `state` the page state (state.info).

import { manualUndo } from './mode-transaction.js';

export function createUndoTables({ api, state, wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms)) }) {
  // What a step records before it runs and how it is undone (the mode switch is a transaction, see core/mode-transaction.js).
  // snapshot(env) returns plain JSON of what the step is about to change; undo(before) looks at the state NOW and puts back only what
  // differs from the record: 'undone', or 'unchanged' when it found the state as recorded. A step only the person can reverse has no undo.

  const settingBack = (key) => ({
    snapshot: (env) => ({ v: !!env.settings[key] }),
    undo: async (before) => {
      if (!before) throw new Error('no record');
      const now = await api.getSettings();
      if (!!now?.[key] === before.v) return 'unchanged';
      await api.setSetting(key, before.v);
      return 'undone';
    }
  });

  const startupModes = async () => (await api.getStartup()) ?? [];

  const SETUP_UNDO = {
    integration: {
      snapshot: (env) => ({ m: env.settings.integrationMode ?? 'light' }),
      undo: async (before) => {
        if (!before) throw new Error('no record');
        const now = await api.getSettings();
        if (now?.integrationMode === before.m) return 'unchanged';
        const result = await api.setIntegrationMode(before.m);
        state.info.integrationMode = result?.mode ?? before.m;
        return 'undone';
      }
    },
    autostart: {
      // Only the entries the suggestions would move are recorded (the record stays small).
      snapshot: async (env) => ({ r: !!env.settings.runStartupApps, e: (await startupModes()).filter((e) => !e.disabled && e.mode !== e.suggested).slice(0, 24).map((e) => [e.id, e.mode]) }),
      undo: async (before) => {
        if (!before) throw new Error('no record');
        let changed = false;
        const now = await startupModes();
        for (const [id, mode] of before.e ?? []) {
          if (now.find((e) => e.id === id)?.mode !== mode && now.some((e) => e.id === id)) {
            await api.setStartupMode(id, mode);
            changed = true;
          }
        }
        if (!!(await api.getSettings())?.runStartupApps !== before.r) {
          await api.setSetting('runStartupApps', before.r);
          changed = true;
        }
        return changed ? 'undone' : 'unchanged';
      }
    },
    services: {
      // `taskInstalled` of the host only says the service is healthy; it is true before and after, so it tells nothing about what the setup
      // changed. What counts: which services were selected (k: that selection was applied, nothing pending) and then the host's own apply.
      snapshot: async () => {
        const data = await api.getServices();
        return { k: !data?.pending, s: (data?.services ?? []).filter((x) => x.selected).slice(0, 24).map((x) => x.name) };
      },
      undo: async (before) => {
        if (!before) throw new Error('no record');
        const data = await api.getServices();
        const wanted = new Set(before.s ?? []);
        let changed = false;
        for (const entry of data?.services ?? []) {
          if (!!entry.selected !== wanted.has(entry.name)) {
            await api.toggleCouchService(entry.name);
            changed = true;
          }
        }
        // Applied again by the host (it resumes what is no longer selected and pauses what was selected before; with an empty selection it
        // resumes all and empties the installed list), not only ticked in the list.
        if (changed || (data?.pending && before.k)) {
          await api.setupServiceTask(true);
          changed = true;
        }
        return changed ? 'undone' : 'unchanged';
      }
    },
    xbox: settingBack('guideTapInGame'),
    suppress: settingBack('suppressCompetitors'),
    lock: {
      // c: a couch code was held before. Switching the lock off removes the service's couch code, which cannot be put back.
      snapshot: (env) => ({ m: env.security.couchLock ?? 'off', c: !!env.security.hasPasscode }),
      undo: async (before) => {
        if (!before) throw new Error('no record');
        const now = await api.getSecurity();
        if (now?.couchLock === before.m) return 'unchanged';
        await api.setCouchLock(before.m);
        // Not "back as it was": the old code is gone, a new one has to be set (reported as a leftover, never as done).
        if (before.c && before.m === 'off' && !(await api.getSecurity())?.hasPasscode) throw manualUndo('lockCode');
        return 'undone';
      }
    },
    shell: {
      snapshot: (env) => ({ s: !!env.settings.isShell }),
      undo: async (before) => {
        if (!before) throw new Error('no record');
        const now = await api.getSettings();
        if (!!now?.isShell === before.s) return 'unchanged';
        state.info.isShell = !!(await api.setShell(before.s));
        return 'undone';
      }
    },
    autologon: {
      snapshot: (env) => ({ on: !!env.security.autoLogon }),
      undo: async (before) => {
        if (!before) throw new Error('no record');
        const now = await api.getSecurity();
        if (before.on || !now?.autoLogon || now.autoLogonState !== 'couchhud') return 'unchanged';
        await api.setAutoLogon('off');
        return 'undone';
      }
    }
  };

  const REVERT_UNDO = {
    // The way back to the shell, step by step. Only the automatic sign-in (a password) has no undo: it is named as a leftover and the text says
    // how to set it up again. What a step did NOT change is found unchanged and never listed: the undo compares with the record first.
    release: {
      // The lock's Windows policies are set by the host only while the couch is locked (Hud.SyncLockState: Hardening.Set(locked)); the setup's
      // hold keeps the couch open, so they cannot come back before the hold ends. This undo therefore puts back what they depend on (the lock,
      // the full mode and the helper tasks, by the undos of the steps after it) and checks that it is in place; the host sets the policies
      // again when the hold ends and the couch locks. If the lock is not back they are a leftover.
      snapshot: async () => ({ p: !!(await api.getServices())?.policiesSet }),
      undo: async (before) => {
        if (!before) throw new Error('no record');
        if (!before.p || (await api.getServices())?.policiesSet) return 'unchanged';
        const lock = (await api.getSecurity())?.couchLock;
        // taskInstalled is the service's health: without the service the host cannot set the policies at all.
        if ((await api.getServices())?.taskInstalled === false || (await api.getSettings())?.integrationMode !== 'full' || !lock || lock === 'off') throw new Error('policies not back');
        // Not "undone": the policies are set later, when the couch locks.
        return 'later';
      }
    },
    services: {
      // The paused services and the helper tasks of the shell setup. The tasks need the full mode (the undo of the last step ran first).
      snapshot: async () => {
        const data = await api.getServices();
        return { k: !!data?.taskInstalled, s: (data?.services ?? []).filter((x) => x.selected).slice(0, 12).map((x) => x.name) };
      },
      undo: async (before) => {
        if (!before) throw new Error('no record');
        const data = await api.getServices();
        let changed = false;
        for (const name of before.s ?? []) {
          const entry = (data?.services ?? []).find((x) => x.name === name);
          if (entry && !entry.selected) {
            await api.toggleCouchService(name);
            changed = true;
          }
        }
        // `taskInstalled` only says the service is healthy: it stays true after the way back. What counts is the selection that was applied
        // (data.pending: it differs from the selection now), so the host applies it again (pauses the services, records them as installed).
        if (before.k && (changed || data?.pending)) {
          await api.setupServiceTask(true);
          changed = true;
        }
        return changed ? 'undone' : 'unchanged';
      }
    },
    profiles: {
      snapshot: async () => ({ p: (await startupModes()).filter((e) => e.mode === 'pc').slice(0, 24).map((e) => e.id) }),
      undo: async (before) => {
        if (!before) throw new Error('no record');
        const now = await startupModes();
        let changed = false;
        for (const id of before.p ?? []) {
          const entry = now.find((e) => e.id === id);
          if (entry && entry.mode !== 'pc') {
            await api.setStartupMode(id, 'pc');
            changed = true;
          }
        }
        return changed ? 'undone' : 'unchanged';
      }
    },
    suppress: settingBack('suppressCompetitors'),
    light: {
      snapshot: (env) => {
        // A lock that could not be read is not "off": no record, and the undo then refuses instead of guessing.
        const lock = env.security?.couchLock;
        if (!['off', 'code', 'windows'].includes(lock)) throw new Error('lock not read');
        return { m: env.settings.integrationMode ?? 'light', s: !!env.settings.isShell, l: lock };
      },
      undo: async (before) => {
        if (!before) throw new Error('no record');
        const now = await api.getSettings();
        let changed = false;
        if (now?.integrationMode !== before.m) {
          const result = await api.setIntegrationMode(before.m);
          state.info.integrationMode = result?.mode ?? before.m;
          changed = true;
        }
        if (before.s && !(await api.getSettings())?.isShell) {
          state.info.isShell = !!(await api.setShell(true));
          changed = true;
        }
        // The switch to the light mode drops the couch lock with it: it goes back too, and when it cannot (the old code is gone) that is a
        // leftover, never a quiet loss of the protection.
        if (before.l && before.l !== 'off' && (await api.getSecurity())?.couchLock !== before.l) {
          try {
            await api.setCouchLock(before.l);
          } catch {
            // judged by the state below
          }
          if ((await api.getSecurity())?.couchLock !== before.l) throw manualUndo(before.l === 'code' ? 'lockCode' : 'lockMode');
          changed = true;
        }
        return changed ? 'undone' : 'unchanged';
      }
    }
  };
  return { SETUP_UNDO, REVERT_UNDO };
}
