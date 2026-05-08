namespace MediaModule.Domain.Entities;

public enum DuplicateResolutionAction
{
    SaveAsNew = 0,
    ChooseAnotherOrder = 1,
    CancelSave = 2,
    ReplaceExisting = 3,
}
