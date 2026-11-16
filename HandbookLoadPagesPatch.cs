using HarmonyLib;
using Vintagestory.GameContent;

namespace AstrosHandbookLoadingFix;

[HarmonyPatch(typeof(GuiDialogHandbook), nameof(GuiDialogHandbook.Dispose))]
public static class HandbookDisposePatch
{
    public static void Prefix(GuiDialogHandbook __instance) => HandbookPageLoader.Dispose(__instance);
}

[HarmonyPatch(typeof(GuiDialogHandbook), "initOverviewGui")]
public static class HandbookOverviewPatch
{
    public static void Postfix(GuiDialogHandbook __instance) => HandbookPageLoader.UpdateStatus(__instance);
}
