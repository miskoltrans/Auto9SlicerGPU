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
        public Texture2D LuminanceTexture;

        // Collapsed image data for debug score queries
        public Color32[] CollapsedPixels;
        public int CollapsedWidth, CollapsedHeight;
        public byte MaxAlpha;

        // Per-corner second-axis results
        public int AlphaStartA, AlphaStartB, AlphaEndA, AlphaEndB;   // Pass 1: alpha shape
        public int EdgeStartA, EdgeStartB, EdgeEndA, EdgeEndB;       // Pass 2: inner edge
        public int HeightStartA, HeightStartB, HeightEndA, HeightEndB; // Combined (max)

        // Final border in Unity order (relative to cropped image)
        public Border FinalBorder;

        /// <summary>
        /// True if the asset has meaningful corners and can be 9-sliced.
        /// False if no corners were detected (e.g., a flat bar) — don't cut.
        /// </summary>
        public bool IsValid =>
            Crop.IsValid &&
            (XAxis.HasCenter || YAxis.HasCenter) &&
            (HeightStartA > 0 || HeightStartB > 0 || HeightEndA > 0 || HeightEndB > 0);
    }
}
