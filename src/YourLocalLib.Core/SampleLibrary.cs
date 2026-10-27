namespace YourLocalLib.Core;

public static class SampleLibrary
{
    public static void Insert(LibraryDatabase db)
    {
        var bookcase = db.InsertLocationRaw("Sample Bookcase", null, true);
        var top = db.InsertLocationRaw("Top shelf", bookcase, true);
        var lower = db.InsertLocationRaw("Lower shelf", bookcase, true);
        db.InsertTitleRaw("SAMPLE: The Teacup That Wasn't There", "A. Sampleton", "9780000000019", "0000000010", 1, top, true, "Clearly Fake");
        db.InsertTitleRaw("SAMPLE: Rain on a Paper Moon", "L. Invented", "9780000000026", "0000000029", 1, top, true);
        db.InsertTitleRaw("SAMPLE: A Dictionary of Imaginary Kettles", "M. Notreal", "9780000000033", "0000000037", 1, lower, true);
        db.InsertTitleRaw("SAMPLE: Omnibus of Nothing in Particular", "N. Nobody", null, null, 1, null, true);
        db.InsertTitleRaw("SAMPLE: The Second Copy", "A. Sampleton", "9780000000040", "0000000045", 2, lower, true);
    }
}
