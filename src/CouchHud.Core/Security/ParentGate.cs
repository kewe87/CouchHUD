using CouchHud.Core.Guard;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Security;

/// <summary>A parents' grant that fits and is used up only once the work it was given for is ready (<see cref="ParentGate.Use"/>).</summary>
public readonly record struct ParentTicket(string Action, string? Item);

/// <summary>
/// The check and the use of the parents' one-time grant, as the HUD applies them. <see cref="Check"/> only looks (a refused or failed change does
/// not use the grant up); <see cref="Use"/> consumes it and refuses when it is gone (run out, or used by a call in between), so one code never
/// pays for two executions. Without a parents' code the unlocked couch is enough (<paramref name="requireUnlocked"/>).
/// </summary>
public sealed class ParentGate(ParentGrant grant, Func<bool> hasParentCode, Action requireUnlocked)
{
    public ParentTicket? Check(string action, string? item, string? reason = null)
    {
        if (!hasParentCode())
        {
            requireUnlocked();
            return null;
        }
        // The page is told which grant to ask for (action and item) and repeats the call (ParentCodeNeededException, UiBridge).
        if (!grant.Fits(action, item)) throw new ParentCodeNeededException(Strings.Get("parental.codeRequired"), action, item) { Reason = reason };
        return new ParentTicket(action, item);
    }

    public void Use(ParentTicket? ticket)
    {
        if (ticket is { } t && !grant.TryConsume(t.Action, t.Item)) throw new ParentCodeNeededException(Strings.Get("parental.codeRequired"), t.Action, t.Item);
    }
}
