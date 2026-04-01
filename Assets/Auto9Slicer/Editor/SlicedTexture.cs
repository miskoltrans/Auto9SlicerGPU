using UnityEngine;

namespace Auto9Slicer
{
    public class SlicedTexture
    {
        public SlicedTexture(Texture2D texture, Border border)
        {
            Texture = texture;
            Border = border;
        }

        public Texture2D Texture { get; }
        public Border Border { get; }
    }

    public struct Border
    {
        public Border(int left, int bottom, int right, int top)
        {
            Left = left;
            Bottom = bottom;
            Right = right;
            Top = top;
        }

        public Vector4 ToVector4()
        {
            return new Vector4(Left, Bottom, Right, Top);
        }

        public int Left { get; }
        public int Bottom { get; }
        public int Right { get; }
        public int Top { get; }
    }

    public enum CornerMatchMode
    {
        AlphaFixedHeight,    // alpha-only scoring, template height = border width
        AlphaBruteForce,     // alpha-only scoring, sweep template heights
        ColorFixedHeight,    // full RGBA scoring, template height = border width
        ColorBruteForce      // full RGBA scoring, sweep template heights
    }

    public struct CropBounds
    {
        public int XMin, YMin, XMax, YMax;
        public int Width => XMax - XMin + 1;
        public int Height => YMax - YMin + 1;
        public bool IsValid => XMax >= XMin && YMax >= YMin;
    }

    public struct AxisAnalysis
    {
        public int CenterStart;
        public int CenterEnd;
        public int BorderStart;
        public int BorderEnd;

        public int CenterSize => CenterEnd > CenterStart ? CenterEnd - CenterStart + 1 : 0;
        public bool HasCenter => CenterEnd > CenterStart;
    }

    public class SliceAnalysis
    {
        public int OriginalWidth;
        public int OriginalHeight;
        public CropBounds Crop;
        public AxisAnalysis XAxis;
        public AxisAnalysis YAxis;
        public bool XDominant;
        public Texture2D CroppedTexture;
        public Texture2D CollapsedTexture;

        // Second-axis results (from per-corner template matching)
        public int SecondAxisStart;  // top (if X dominant) or left (if Y dominant)
        public int SecondAxisEnd;    // bottom (if X dominant) or right (if Y dominant)
        public bool DirectionAWon;   // true = start-side was reference
        public double ScoreA, ScoreALeft, ScoreARight;
        public double ScoreB, ScoreBLeft, ScoreBRight;
        public int HeightALeft, HeightARight;  // best template heights for direction A
        public int HeightBLeft, HeightBRight;  // best template heights for direction B

        // Final border in Unity order (relative to cropped image)
        public Border FinalBorder;
    }
}
