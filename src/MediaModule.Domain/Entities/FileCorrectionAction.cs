namespace MediaModule.Domain.Entities;

public enum FileCorrectionAction
{
    None = 0,
    AcceptAndMove = 1,
    CancelProcessing = 2,
    BackToOrderSelection = 3,
}
