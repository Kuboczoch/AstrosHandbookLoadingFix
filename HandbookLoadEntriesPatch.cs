using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace AstrosHandbookLoadingFix;

[HarmonyPatch(typeof(GuiDialogHandbook), "loadEntries")]
public static class HandbookLoadEntriesPatch
{
    public static void Prefix(GuiDialogHandbook __instance) => HandbookPageLoader.Restart(__instance);
    public static void Postfix(GuiDialogHandbook __instance) => HandbookPageLoader.GuidesReady(__instance);

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original = AccessTools.Method(typeof(TyronThreadPool), "QueueTask", [typeof(Action), typeof(string)]);
        var replacement = AccessTools.Method(typeof(HandbookPageLoader), nameof(HandbookPageLoader.Schedule));
        int replacements = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(original))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                replacements++;
            }
            yield return instruction;
        }
        if (replacements != 1) throw new InvalidOperationException($"Expected one handbook worker scheduling call; found {replacements}.");
    }
}
