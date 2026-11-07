// Synthetic logical-capture fixture, derived from TVDemo GroupBoxDemo; maintained by Workbench.
using TSharpVision.Constants;

namespace TSharpWorkbench.Tests.Infrastructure;

internal static class GroupBoxCaptureFixture
{
    internal static TDialog CreateGroupBoxDemoDialog(TRect bounds)
    {
        var dialog = new TDialog(bounds, "Group boxes");
        dialog.Insert(new TStaticText(new TRect(2, 1, 61, 2),
            "Synthetic disks | Tab / Shift+Tab move between controls"));

        var disks = new TGroupBox(new TRect(2, 3, 30, 11), "Disk to Compress:");
        disks.Insert(new TRadioButtons(new TRect(2, 2, 25, 5),
            new TSItem("~C~:  System", new TSItem("~D~:  Data",
                new TSItem("~E~:  Archive", null)))));
        disks.Insert(new TCheckBoxes(new TRect(2, 6, 25, 7), new TSItem("Verify after copy", null)));
        dialog.Insert(disks);

        var options = new TGroupBox(new TRect(32, 3, 61, 11), "Options")
            { TitleAlignment = TGroupBoxTitleAlignment.Center };
        var label = new TGroupBox(new TRect(2, 2, 27, 6), "Popis svazku")
            { TitleAlignment = TGroupBoxTitleAlignment.Right };
        var input = new TInputLine(new TRect(2, 2, 23, 3), 40);
        input.SetData("Sample archive");
        label.Insert(input);
        options.Insert(label);
        options.Insert(new TCheckBoxes(new TRect(2, 6, 26, 7), new TSItem("Keep original files", null)));
        dialog.Insert(options);

        var logical = new TGroupBox(new TRect(2, 12, 61, 13), "") { ShowBorder = false };
        logical.Insert(new TCheckBoxes(new TRect(0, 0, 28, 1), new TSItem("Details (borderless)", null)));
        logical.Insert(new TCheckBoxes(new TRect(31, 0, 58, 1), new TSItem("Remember selection", null)));
        dialog.Insert(logical);
        dialog.Insert(new TStaticText(new TRect(2, 14, 61, 15), "No disk access. Titles are presentation text."));
        dialog.Insert(new TButton(new TRect(26, 16, 38, 18), "~C~lose", Views.cmCancel, ButtonConstants.bfDefault));
        dialog.SetCurrent(disks, TView.selectMode.normalSelect);
        return dialog;
    }

}
