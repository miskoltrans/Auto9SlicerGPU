using System.Linq;
using UnityEngine;

namespace Auto9Slicer
{
	public static class Slicer
	{
		public static SlicedTexture Slice(Texture2D texture, SliceOptions options)
		{
			var analysis = Analyze(texture, options.Margin, CornerMatchMode.ColorFixedHeight);
			var cutTexture = CutCenter(analysis, Mathf.Max(1, options.CenterSize));
			return new SlicedTexture(cutTexture ?? texture, analysis.FinalBorder);
		}

		/// <summary>Legacy slice using the original pixel-perfect algorithm.</summary>
		public static SlicedTexture SliceLegacy(Texture2D texture, SliceOptions options)
		{
			return (new Runner(texture, options).Run());
		}

		/// <summary>
		/// Generate a texture with the dominant axis center cut to centerSize pixels.
		/// The result is alpha-cropped and center-reduced, ready for 9-slice import.
		/// </summary>
		public static Texture2D CutCenter(SliceAnalysis analysis, int centerSize = 1)
		{
			var src = analysis.CroppedTexture;
			if (src == null) return null;

			var srcPixels = src.GetPixels32();
			var srcW = src.width;
			var srcH = src.height;

			var dominant = analysis.XDominant ? analysis.XAxis : analysis.YAxis;
			if (!dominant.HasCenter) return src;

			// How many center pixels to remove (keep centerSize)
			var centerTotal = dominant.CenterEnd - dominant.CenterStart + 1;
			var toRemove = Mathf.Max(0, centerTotal - centerSize);
			if (toRemove == 0) return src;

			// Cutpoints: keep [0, CenterStart + centerSize), skip, keep [CenterEnd + 1, length)
			var keepEnd = dominant.CenterStart + centerSize;
			var resumeAt = dominant.CenterEnd + 1;

			if (analysis.XDominant)
			{
				var newW = srcW - toRemove;
				var outPixels = new Color32[newW * srcH];
				for (var y = 0; y < srcH; y++)
				{
					var outX = 0;
					for (var x = 0; x < srcW; x++)
					{
						if (x >= keepEnd && x < resumeAt) continue;
						outPixels[y * newW + outX] = srcPixels[y * srcW + x];
						outX++;
					}
				}

				var tex = new Texture2D(newW, srcH, TextureFormat.RGBA32, false);
				tex.SetPixels32(outPixels);
				tex.Apply();
				return tex;
			}
			else
			{
				var newH = srcH - toRemove;
				var outPixels = new Color32[srcW * newH];
				var outY = 0;
				for (var y = 0; y < srcH; y++)
				{
					if (y >= keepEnd && y < resumeAt) continue;
					for (var x = 0; x < srcW; x++)
						outPixels[outY * srcW + x] = srcPixels[y * srcW + x];
					outY++;
				}

				var tex = new Texture2D(srcW, newH, TextureFormat.RGBA32, false);
				tex.SetPixels32(outPixels);
				tex.Apply();
				return tex;
			}
		}

		public static SliceAnalysis Analyze(Texture2D texture, int margin = 0,
			CornerMatchMode cornerMode = CornerMatchMode.ColorFixedHeight)
		{
			return new Analyzer(texture, margin, cornerMode).Run();
		}

		private class Runner
		{
			private readonly Texture2D _texture;
			private readonly SliceOptions _options;
			private int _width;
			private int _height;
			private Color32[] _pixels;

			public Runner(Texture2D texture, SliceOptions options)
			{
				_texture = texture;
				_options = options;
			}

			public SlicedTexture Run()
			{
				_width = _texture.width;
				_height = _texture.height;
				_pixels = _texture.GetPixels().Select(x => (Color32) x).ToArray();
				for (var i = 0; i < _pixels.Length; ++i) _pixels[i] = _pixels[i].a > _options.AlphaThreshold ? _pixels[i] : (Color32) Color.clear;

				var xDiffList = CalcDiffList(_width, _height, 1, _width);
				var (xStart, xEnd) = CalcLine(xDiffList);

				var yDiffList = CalcDiffList(1, _width, _width, _height);
				if (_options.GradientAware)
					yDiffList = MakeGradientAware(yDiffList, _width);
				var (yStart, yEnd) = CalcLine(yDiffList);

				var skipX = (xStart == 0 && xEnd == 0);
				var skipY = (yStart == 0 && yEnd == 0);
				var output = GenerateSlicedTexture(xStart, xEnd, yStart, yEnd, skipX, skipY);

				var left = xStart;
				var bottom = yStart;
				var right = (_width - xEnd) - 1;
				var top = (_height - yEnd) - 1;

				if (skipX)
				{
					left = 0;
					right = 0;
				}

				if (skipY)
				{
					top = 0;
					bottom = 0;
				}

				return new SlicedTexture(output, new Border(left, bottom, right, top));
			}

			private ulong[] CalcDiffList(int lineDelta, int lineLength, int lineSeek, int length)
			{
				var diffList = new ulong[length];
				diffList[0] = ulong.MaxValue;

				for (var i = 1; i < length; ++i)
				{
					ulong diff = 0;
					var current = i * lineSeek;
					for (var j = 0; j < lineLength; ++j)
					{
						var prev = current - lineSeek;
						diff += (ulong) Diff(_pixels[prev], _pixels[current]);
						current += lineDelta;
					}
					diffList[i] = diff;
				}

				return diffList;
			}

			private int Diff(Color32 a, Color32 b)
			{
				var rd = Mathf.Abs(a.r - b.r);
				var gd = Mathf.Abs(a.g - b.g);
				var bd = Mathf.Abs(a.b - b.b);
				var ad = Mathf.Abs(a.a - b.a);
				if (rd <= _options.Tolerate) rd = 0;
				if (gd <= _options.Tolerate) gd = 0;
				if (bd <= _options.Tolerate) bd = 0;
				if (ad <= _options.Tolerate) ad = 0;
				return rd + gd + bd + ad;
			}

			private (int Start, int End) CalcLine(ulong[] list)
			{
				var start = 0;
				var end = 0;
				var tmpStart = 0;
				var tmpEnd = 0;
				for (var i = 0; i < list.Length; ++i)
				{
					if (list[i] == 0)
					{
						tmpEnd = i;
						continue;
					}

					if (end - start < tmpEnd - tmpStart)
					{
						start = tmpStart;
						end = tmpEnd;
					}

					tmpStart = i;
					tmpEnd = i;
				}

				if (end - start < tmpEnd - tmpStart)
				{
					start = tmpStart;
					end = tmpEnd;
				}

				start += _options.Margin;
				end -= _options.Margin;

				if (end <= start)
				{
					start = 0;
					end = 0;
				}

				return (start, end);
			}

			private ulong[] MakeGradientAware(ulong[] diffList, int lineLength)
			{
				var result = new ulong[diffList.Length];
				result[0] = ulong.MaxValue;

				var scaledTol = (ulong)_options.GradientTolerate * (ulong)lineLength;

				for (var i = 1; i < diffList.Length; ++i)
				{
					if (diffList[i] == 0)
					{
						result[i] = 0;
						continue;
					}

					if (i < 2 || diffList[i - 1] == ulong.MaxValue)
					{
						result[i] = diffList[i];
						continue;
					}

					var rate = diffList[i] > diffList[i - 1]
						? diffList[i] - diffList[i - 1]
						: diffList[i - 1] - diffList[i];

					result[i] = rate <= scaledTol ? 0 : rate;
				}

				return result;
			}

			private Texture2D GenerateSlicedTexture(int xStart, int xEnd, int yStart, int yEnd, bool skipX, bool skipY)
			{
				var outputWidth = _width - (xEnd - xStart) + (skipX ? 0 : _options.CenterSize - 1);
				var outputHeight = _height - (yEnd - yStart) + (skipY ? 0 : _options.CenterSize - 1);
				var outputPixels = new Color[outputWidth * outputHeight];
				for (int x = 0, originalX = 0; x < outputWidth; ++x, ++originalX)
				{
					if (originalX == xStart && !skipX) originalX += (xEnd - xStart) - _options.CenterSize + 1;
					for (int y = 0, originalY = 0; y < outputHeight; ++y, ++originalY)
					{
						if (originalY == yStart && !skipY) originalY += (yEnd - yStart) - _options.CenterSize + 1;
						outputPixels[y * outputWidth + x] = Get(originalX, originalY);
					}
				}

				var output = new Texture2D(outputWidth, outputHeight);
				output.SetPixels(outputPixels);
				return output;
			}

			private Color32 Get(int x, int y)
			{
				return _pixels[y * _width + x];
			}
		}

		private class Analyzer
		{
			private readonly Texture2D _texture;
			private readonly int _margin;
			private readonly CornerMatchMode _cornerMode;
			private Color32[] _pixels;
			private int _width, _height;

			public Analyzer(Texture2D texture, int margin, CornerMatchMode cornerMode)
			{
				_texture = texture;
				_margin = margin;
				_cornerMode = cornerMode;
			}

			public SliceAnalysis Run()
			{
				_width = _texture.width;
				_height = _texture.height;
				_pixels = _texture.GetPixels32();

				var analysis = new SliceAnalysis
				{
					OriginalWidth = _width,
					OriginalHeight = _height
				};

				// Step 1: Alpha crop
				analysis.Crop = FindAlphaBounds();
				if (!analysis.Crop.IsValid) return analysis;

				ApplyCrop(analysis.Crop);
				analysis.CroppedTexture = CreateTexture(_pixels, _width, _height);

				// Step 2: Analyze both axes (tolerance 0, margin 0)
				var xDiffs = CalcDiffList(true);
				analysis.XAxis = FindCenter(xDiffs, _width);

				var yDiffs = CalcDiffList(false);
				analysis.YAxis = FindCenter(yDiffs, _height);

				analysis.XDominant = analysis.XAxis.CenterSize >= analysis.YAxis.CenterSize;

				// Symmetrize dominant axis borders: if they differ by <= margin, use the bigger one
				if (analysis.XDominant && analysis.XAxis.HasCenter)
					analysis.XAxis = SymmetrizeBorders(analysis.XAxis, _width);
				else if (!analysis.XDominant && analysis.YAxis.HasCenter)
					analysis.YAxis = SymmetrizeBorders(analysis.YAxis, _height);

				// Step 3: Collapse center on dominant axis
				if (analysis.XDominant && analysis.XAxis.HasCenter)
					CollapseX(analysis.XAxis.CenterStart, analysis.XAxis.CenterEnd);
				else if (!analysis.XDominant && analysis.YAxis.HasCenter)
					CollapseY(analysis.YAxis.CenterStart, analysis.YAxis.CenterEnd);

				if (analysis.XAxis.HasCenter || analysis.YAxis.HasCenter)
					analysis.CollapsedTexture = CreateTexture(_pixels, _width, _height);

				// Step 4: Find second axis borders via per-corner template matching
				if (analysis.XDominant && analysis.XAxis.HasCenter)
					FindSecondAxisBorders(analysis, analysis.XAxis.BorderStart, analysis.XAxis.BorderEnd, true);
				else if (!analysis.XDominant && analysis.YAxis.HasCenter)
					FindSecondAxisBorders(analysis, analysis.YAxis.BorderStart, analysis.YAxis.BorderEnd, false);

				return analysis;
			}

			private CropBounds FindAlphaBounds()
			{
				var bounds = new CropBounds
				{
					XMin = _width, YMin = _height, XMax = -1, YMax = -1
				};

				for (var y = 0; y < _height; y++)
				for (var x = 0; x < _width; x++)
				{
					if (_pixels[y * _width + x].a <= 0) continue;
					if (x < bounds.XMin) bounds.XMin = x;
					if (x > bounds.XMax) bounds.XMax = x;
					if (y < bounds.YMin) bounds.YMin = y;
					if (y > bounds.YMax) bounds.YMax = y;
				}

				return bounds;
			}

			private void ApplyCrop(CropBounds crop)
			{
				var newWidth = crop.Width;
				var newHeight = crop.Height;
				var newPixels = new Color32[newWidth * newHeight];

				for (var y = 0; y < newHeight; y++)
				for (var x = 0; x < newWidth; x++)
					newPixels[y * newWidth + x] = _pixels[(y + crop.YMin) * _width + (x + crop.XMin)];

				_pixels = newPixels;
				_width = newWidth;
				_height = newHeight;
			}

			private ulong[] CalcDiffList(bool isXAxis)
			{
				int lineDelta, lineLength, lineSeek, count;
				if (isXAxis)
				{
					lineDelta = _width;
					lineLength = _height;
					lineSeek = 1;
					count = _width;
				}
				else
				{
					lineDelta = 1;
					lineLength = _width;
					lineSeek = _width;
					count = _height;
				}

				var diffs = new ulong[count];
				diffs[0] = ulong.MaxValue;

				for (var i = 1; i < count; i++)
				{
					ulong diff = 0;
					var curr = i * lineSeek;
					for (var j = 0; j < lineLength; j++)
					{
						var prev = curr - lineSeek;
						var a = _pixels[prev];
						var b = _pixels[curr];
						diff += (ulong)(Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) +
						                Mathf.Abs(a.b - b.b) + Mathf.Abs(a.a - b.a));
						curr += lineDelta;
					}

					diffs[i] = diff;
				}

				return diffs;
			}

			private AxisAnalysis FindCenter(ulong[] diffs, int axisLength)
			{
				var start = 0;
				var end = 0;
				var tmpStart = 0;
				var tmpEnd = 0;

				for (var i = 0; i < diffs.Length; i++)
				{
					if (diffs[i] == 0)
					{
						tmpEnd = i;
						continue;
					}

					if (end - start < tmpEnd - tmpStart)
					{
						start = tmpStart;
						end = tmpEnd;
					}

					tmpStart = i;
					tmpEnd = i;
				}

				if (end - start < tmpEnd - tmpStart)
				{
					start = tmpStart;
					end = tmpEnd;
				}

				start += _margin;
				end -= _margin;

				if (end <= start)
					return new AxisAnalysis();

				return new AxisAnalysis
				{
					CenterStart = start,
					CenterEnd = end,
					BorderStart = start,
					BorderEnd = axisLength - end - 1
				};
			}

			private AxisAnalysis SymmetrizeBorders(AxisAnalysis axis, int axisLength)
			{
				var diff = Mathf.Abs(axis.BorderStart - axis.BorderEnd);
				if (diff == 0 || diff > _margin) return axis;

				var bigger = Mathf.Max(axis.BorderStart, axis.BorderEnd);
				var newEnd = axisLength - bigger - 1;
				if (newEnd <= bigger) return axis; // would eliminate the center

				return new AxisAnalysis
				{
					CenterStart = bigger,
					CenterEnd = newEnd,
					BorderStart = bigger,
					BorderEnd = bigger
				};
			}

			/// <summary>
			/// Find second-axis borders using per-corner template matching on the collapsed image.
			/// xDominant=true: borderA=left, borderB=right, scanning Y axis.
			/// xDominant=false: borderA=bottom, borderB=top, scanning X axis.
			/// </summary>
			private void FindSecondAxisBorders(SliceAnalysis result, int borderA, int borderB, bool xDominant)
			{
				// The collapsed image axes:
				// If X dominant: width = borderA + borderB, height = full. Scan along height.
				// If Y dominant: height = borderA + borderB, width = full. Scan along width.
				var scanLength = xDominant ? _height : _width;
				var scanStride = xDominant ? _width : 1;       // step to next row or column
				var cornerStride = xDominant ? 1 : _width;     // step within a row or column

				// Corner column/row ranges in collapsed image
				var aStart = 0;                   // left or bottom side start
				var aEnd = borderA;               // left or bottom side end (exclusive)
				var bStart = borderA;             // right or top side start
				var bEnd = borderA + borderB;     // right or top side end (exclusive)

				// Direction A: start-side corners are reference, find end-side positions
				var (posALeft, hALeft, normALeft) = ScanCorner(
					aStart, aEnd, cornerStride,
					borderA, scanStride, scanLength,
					templateAtStart: true);
				var (posARight, hARight, normARight) = ScanCorner(
					bStart, bEnd, cornerStride,
					borderB, scanStride, scanLength,
					templateAtStart: true);
				var normA = normALeft + normARight;

				// Direction B: end-side corners are reference, find start-side positions
				var (posBLeft, hBLeft, normBLeft) = ScanCorner(
					aStart, aEnd, cornerStride,
					borderA, scanStride, scanLength,
					templateAtStart: false);
				var (posBRight, hBRight, normBRight) = ScanCorner(
					bStart, bEnd, cornerStride,
					borderB, scanStride, scanLength,
					templateAtStart: false);
				var normB = normBLeft + normBRight;

				result.ScoreA = normA;
				result.ScoreALeft = normALeft;
				result.ScoreARight = normARight;
				result.HeightALeft = hALeft;
				result.HeightARight = hARight;
				result.ScoreB = normB;
				result.ScoreBLeft = normBLeft;
				result.ScoreBRight = normBRight;
				result.HeightBLeft = hBLeft;
				result.HeightBRight = hBRight;
				result.DirectionAWon = normA <= normB;

				int secondStart, secondEnd;
				if (result.DirectionAWon)
				{
					// Start-side is reference
					secondStart = Mathf.Max(hALeft, hARight);
					var endFromA = scanLength - posALeft;
					var endFromB = scanLength - posARight;
					secondEnd = Mathf.Max(endFromA, endFromB);
				}
				else
				{
					// End-side is reference
					secondEnd = Mathf.Max(hBLeft, hBRight);
					var startFromA = posBLeft + hBLeft;
					var startFromB = posBRight + hBRight;
					secondStart = Mathf.Max(startFromA, startFromB);
				}

				result.SecondAxisStart = secondStart;
				result.SecondAxisEnd = secondEnd;

				// Compose final border
				// secondStart = border at start of second axis (bottom if X dom, left if Y dom)
				// secondEnd   = border at end of second axis (top if X dom, right if Y dom)
				if (xDominant)
				{
					result.FinalBorder = new Border(
						left: borderA,
						bottom: secondStart,
						right: borderB,
						top: secondEnd);
				}
				else
				{
					result.FinalBorder = new Border(
						left: secondStart,
						bottom: borderA,
						right: secondEnd,
						top: borderB);
				}
			}

			private (int, int, double) ScanCorner(
				int colStart, int colEnd, int colStride,
				int nominalHeight, int lineStride, int totalLines,
				bool templateAtStart)
			{
				var bruteForce = _cornerMode == CornerMatchMode.AlphaBruteForce ||
				                 _cornerMode == CornerMatchMode.ColorBruteForce;
				var alphaOnly = _cornerMode == CornerMatchMode.AlphaFixedHeight ||
				                _cornerMode == CornerMatchMode.AlphaBruteForce;

				var bestNormScore = double.MaxValue;
				var bestPos = templateAtStart ? totalLines - nominalHeight : 0;
				var bestHeight = nominalHeight;
				var pixelWidth = colEnd - colStart;

				var hMin = bruteForce ? Mathf.Max(2, nominalHeight / 2) : nominalHeight;
				var hMax = bruteForce ? Mathf.Min(nominalHeight * 3, totalLines / 2) : nominalHeight;

				for (var h = hMin; h <= hMax; h++)
				{
					var templateStart = templateAtStart ? 0 : totalLines - h;

					int scanMin, scanMax;
					if (templateAtStart)
					{
						scanMin = h;
						scanMax = totalLines - h;
					}
					else
					{
						scanMin = 0;
						scanMax = totalLines - 2 * h;
					}

					for (var scanPos = scanMin; scanPos <= scanMax; scanPos++)
					{
						var rawScore = alphaOnly
							? CompareFlippedAlpha(colStart, colEnd, colStride,
								templateStart, scanPos, h, lineStride)
							: CompareFlippedColor(colStart, colEnd, colStride,
								templateStart, scanPos, h, lineStride);

						var normScore = (double)rawScore / (pixelWidth * h);

						if (normScore < bestNormScore)
						{
							bestNormScore = normScore;
							bestPos = scanPos;
							bestHeight = h;
						}
					}
				}

				return (bestPos, bestHeight, bestNormScore);
			}

			private ulong CompareFlippedAlpha(
				int colStart, int colEnd, int colStride,
				int srcLineStart, int dstLineStart, int lineCount, int lineStride)
			{
				ulong score = 0;
				for (var line = 0; line < lineCount; line++)
				{
					var srcLine = srcLineStart + line;
					var dstLine = dstLineStart + (lineCount - 1 - line);
					var srcBase = srcLine * lineStride;
					var dstBase = dstLine * lineStride;

					for (var col = colStart; col < colEnd; col++)
					{
						var srcIdx = srcBase + col * colStride;
						var dstIdx = dstBase + col * colStride;
						score += (ulong)Mathf.Abs(_pixels[srcIdx].a - _pixels[dstIdx].a);
					}
				}

				return score;
			}

			private ulong CompareFlippedColor(
				int colStart, int colEnd, int colStride,
				int srcLineStart, int dstLineStart, int lineCount, int lineStride)
			{
				ulong score = 0;
				for (var line = 0; line < lineCount; line++)
				{
					var srcLine = srcLineStart + line;
					var dstLine = dstLineStart + (lineCount - 1 - line);
					var srcBase = srcLine * lineStride;
					var dstBase = dstLine * lineStride;

					for (var col = colStart; col < colEnd; col++)
					{
						var srcIdx = srcBase + col * colStride;
						var dstIdx = dstBase + col * colStride;
						var a = _pixels[srcIdx];
						var b = _pixels[dstIdx];
						score += (ulong)(Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) +
						                 Mathf.Abs(a.b - b.b) + Mathf.Abs(a.a - b.a));
					}
				}

				return score;
			}

			private void CollapseX(int centerStart, int centerEnd)
			{
				var newWidth = _width - (centerEnd - centerStart + 1);
				var newPixels = new Color32[newWidth * _height];

				for (var y = 0; y < _height; y++)
				{
					var outX = 0;
					for (var x = 0; x < _width; x++)
					{
						if (x >= centerStart && x <= centerEnd) continue;
						newPixels[y * newWidth + outX] = _pixels[y * _width + x];
						outX++;
					}
				}

				_pixels = newPixels;
				_width = newWidth;
			}

			private void CollapseY(int centerStart, int centerEnd)
			{
				var newHeight = _height - (centerEnd - centerStart + 1);
				var newPixels = new Color32[_width * newHeight];

				var outY = 0;
				for (var y = 0; y < _height; y++)
				{
					if (y >= centerStart && y <= centerEnd) continue;
					for (var x = 0; x < _width; x++)
						newPixels[outY * _width + x] = _pixels[y * _width + x];
					outY++;
				}

				_pixels = newPixels;
				_height = newHeight;
			}

			private static Texture2D CreateTexture(Color32[] pixels, int width, int height)
			{
				var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
				tex.SetPixels32(pixels);
				tex.Apply();
				return tex;
			}
		}
	}
}
