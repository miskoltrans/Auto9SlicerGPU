using System.Linq;
using UnityEngine;

namespace Auto9Slicer
{
	public static class Slicer
	{
		public static SlicedTexture Slice(Texture2D texture, SliceOptions options)
		{
			var analysis = Analyze(texture, options.Margin);
			if (!analysis.IsValid)
				return new SlicedTexture(texture, new Border(0, 0, 0, 0));
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

		public static SliceAnalysis Analyze(Texture2D texture, int margin = 0)
		{
			return new Analyzer(texture, margin).Run();
		}

		/// <summary>
		/// Analyze from raw PNG file path — avoids Unity texture compression artifacts.
		/// </summary>
		public static SliceAnalysis AnalyzeFromFile(string assetPath, int margin = 0)
		{
			var fullPath = System.IO.Path.Combine(
				System.IO.Path.GetDirectoryName(UnityEngine.Application.dataPath) ?? "", assetPath);
			var bytes = System.IO.File.ReadAllBytes(fullPath);
			var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
			tex.LoadImage(bytes);
			var result = new Analyzer(tex, margin).Run();
			Object.DestroyImmediate(tex);
			return result;
		}

		/// <summary>
		/// Auto-detect edge threshold from body rows: compute max derivative per row
		/// in the middle third, take average * 3 + 1000.
		/// </summary>
		private static int AutoEdgeThreshold(Color32[] pixels, int w, int h, byte maxAlpha,
			int colStart, int colEnd)
		{
			var midStart = h / 3;
			var midEnd = 2 * h / 3;
			long sum = 0;
			var cnt = 0;
			var midX = (colStart + colEnd) / 2;

			for (var y = midStart; y < midEnd; y++)
			{
				var maxDiff = 0;
				for (var x = colStart; x < midX - 1; x++)
				{
					var pA = pixels[y * w + x];
					var pB = pixels[y * w + x + 1];
					if (pA.a < maxAlpha || pB.a < maxAlpha) continue;
					var diff = Mathf.Abs(
						(299 * pA.r + 587 * pA.g + 114 * pA.b) -
						(299 * pB.r + 587 * pB.g + 114 * pB.b));
					if (diff > maxDiff) maxDiff = diff;
				}
				sum += maxDiff;
				cnt++;
			}

			return cnt > 0 ? (int)(sum / cnt * 3 + 1000) : 1000;
		}

		/// <summary>
		/// For each row, find the first pixel above threshold scanning from the center seam outward.
		/// </summary>
		private static void ComputeRowEdges(Color32[] pixels, int w, int h, byte maxAlpha,
			int colStart, int colEnd, int threshold, int scanOutDir,
			out int[] edgePos, out int[] edgeStrength)
		{
			edgePos = new int[h];
			edgeStrength = new int[h];

			for (var y = 0; y < h; y++)
			{
				edgePos[y] = -1;
				edgeStrength[y] = 0;

				if (scanOutDir < 0)
				{
					for (var x = colEnd - 2; x >= colStart; x--)
					{
						var diff = LumDiff(pixels, w, y, x, maxAlpha);
						if (diff > threshold)
						{
							edgePos[y] = x;
							edgeStrength[y] = diff;
							break;
						}
					}
				}
				else
				{
					for (var x = colStart; x < colEnd - 1; x++)
					{
						var diff = LumDiff(pixels, w, y, x, maxAlpha);
						if (diff > threshold)
						{
							edgePos[y] = x;
							edgeStrength[y] = diff;
							break;
						}
					}
				}
			}
		}

		private static int LumDiff(Color32[] pixels, int w, int y, int x, byte maxAlpha)
		{
			if (x + 1 >= w) return 0;
			var pA = pixels[y * w + x];
			var pB = pixels[y * w + x + 1];
			if (pA.a < maxAlpha || pB.a < maxAlpha) return 0;
			var lumA = 299 * pA.r + 587 * pA.g + 114 * pA.b;
			var lumB = 299 * pB.r + 587 * pB.g + 114 * pB.b;
			return Mathf.Abs(lumA - lumB);
		}

		/// <summary>
		/// Generate debug texture showing per-row strongest edge.
		/// White dot = edge pixel, brightness = edge strength. Red line = detected border.
		/// </summary>
		public static Texture2D GenerateEdgeTexture(SliceAnalysis analysis, int edgeThreshold = 0)
		{
			if (analysis.CollapsedPixels == null) return null;

			var pixels = analysis.CollapsedPixels;
			var w = analysis.CollapsedWidth;
			var h = analysis.CollapsedHeight;
			var borderA = analysis.XDominant ? analysis.XAxis.BorderStart : analysis.YAxis.BorderStart;
			var borderB = analysis.XDominant ? analysis.XAxis.BorderEnd : analysis.YAxis.BorderEnd;

			var threshA = edgeThreshold >= 0 ? edgeThreshold : AutoEdgeThreshold(pixels, w, h, analysis.MaxAlpha, 0, borderA);
			var threshB = edgeThreshold >= 0 ? edgeThreshold : AutoEdgeThreshold(pixels, w, h, analysis.MaxAlpha, borderA, borderA + borderB);

			ComputeRowEdges(pixels, w, h, analysis.MaxAlpha, 0, borderA, threshA, -1,
				out var posA, out var strA);
			ComputeRowEdges(pixels, w, h, analysis.MaxAlpha, borderA, borderA + borderB, threshB, 1,
				out var posB, out var strB);

			// Per-pixel horizontal luminance derivative
			var outPixels = new Color32[pixels.Length];
			for (var y = 0; y < h; y++)
			for (var x = 0; x < w; x++)
			{
				var idx = y * w + x;
				var p = pixels[idx];
				var diff = LumDiff(pixels, w, y, x, analysis.MaxAlpha);

				if (p.a < analysis.MaxAlpha)
				{
					outPixels[idx] = new Color32(0, 0, 0, p.a > 0 ? (byte)30 : (byte)0);
					continue;
				}

				var thresh = x < borderA ? threshA : threshB;
				var b = (byte)Mathf.Clamp(diff * 255 / 30000, 0, 255);

				if (diff > thresh)
					outPixels[idx] = new Color32(b, b, 0, 255); // yellow = above threshold
				else
					outPixels[idx] = new Color32(0, 0, b, 255); // blue = below threshold
			}

			// Draw cavity boundary lines first (green)
			var (eBL, eBR, eTL, eTR) = EdgeScanBorders(analysis, edgeThreshold);
			var bottomLine = Mathf.Max(eBL, eBR);
			var topLine = Mathf.Max(eTL, eTR);

			if (bottomLine > 0 && bottomLine < h)
				for (var x = 0; x < w; x++)
					outPixels[bottomLine * w + x] = new Color32(0, 200, 0, 255);

			if (topLine > 0)
			{
				var topRow = h - 1 - topLine;
				if (topRow >= 0 && topRow < h)
					for (var x = 0; x < w; x++)
						outPixels[topRow * w + x] = new Color32(100, 255, 100, 255);
			}

			// Then red dots on top
			for (var y = 0; y < h; y++)
			{
				if (posA[y] >= 0)
					outPixels[y * w + posA[y]] = new Color32(255, 0, 0, 255);
				if (posB[y] >= 0)
					outPixels[y * w + posB[y]] = new Color32(255, 0, 0, 255);
			}

			var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
			tex.filterMode = FilterMode.Point;
			tex.SetPixels32(outPixels);
			tex.Apply();
			return tex;
		}

		/// <summary>
		/// Edge line scan: for each corner, scan from edge inward.
		/// Find the last row with a strong edge peak. Border = that row.
		/// Body rows have weak/no peaks (smooth gradient).
		/// </summary>
		public static (int BL, int BR, int TL, int TR) EdgeScanBorders(
			SliceAnalysis analysis, int edgeThreshold = 0)
		{
			if (analysis.CollapsedPixels == null) return (0, 0, 0, 0);

			var pixels = analysis.CollapsedPixels;
			var w = analysis.CollapsedWidth;
			var h = analysis.CollapsedHeight;
			var borderA = analysis.XDominant ? analysis.XAxis.BorderStart : analysis.YAxis.BorderStart;
			var borderB = analysis.XDominant ? analysis.XAxis.BorderEnd : analysis.YAxis.BorderEnd;

			var threshA = edgeThreshold >= 0 ? edgeThreshold : AutoEdgeThreshold(pixels, w, h, analysis.MaxAlpha, 0, borderA);
			var threshB = edgeThreshold >= 0 ? edgeThreshold : AutoEdgeThreshold(pixels, w, h, analysis.MaxAlpha, borderA, borderA + borderB);

			// For each side, compute edge distance from seam per row.
			// Then find the biggest cavity — longest stretch at max distance.

			int[] ComputeEdgeDistances(int colStart, int colEnd, int thresh, int scanOutDir)
			{
				var dist = new int[h];
				for (var y = 0; y < h; y++)
				{
					dist[y] = 0;
					if (scanOutDir < 0)
					{
						for (var x = colEnd - 2; x >= colStart; x--)
						{
							if (LumDiff(pixels, w, y, x, analysis.MaxAlpha) > thresh)
							{ dist[y] = colEnd - 1 - x; break; }
						}
					}
					else
					{
						for (var x = colStart; x < colEnd - 1; x++)
						{
							if (LumDiff(pixels, w, y, x, analysis.MaxAlpha) > thresh)
							{ dist[y] = x - colStart + 1; break; }
						}
					}
				}
				return dist;
			}

			// Find the biggest cavity: longest streak of opening (growing/equal)
			// then closing (shrinking/equal). Like finding the longest balanced parenthesis.
			// Returns (bottomBorder, topBorder).
			(int, int) FindCavity(int[] dist)
			{
				// State: 0 = initial, 1 = opening (growing), 2 = closing (shrinking)
				var bestStart = 0;
				var bestLen = 0;
				var curStart = 0;
				var state = 0; // 0=initial, 1=opening, 2=closing

				for (var y = 1; y < h; y++)
				{
					var diff = dist[y] - dist[y - 1];

					if (diff > 0)
					{
						// Growing
						if (state == 2)
						{
							// Was closing, now growing again → previous cavity ended
							var len = y - curStart;
							if (len > bestLen) { bestLen = len; bestStart = curStart; }
							curStart = y - 1;
						}
						state = 1;
					}
					else if (diff < 0)
					{
						// Shrinking — cavity started at curStart (0 initially, or after last close→open)
						state = 2;
					}
					// diff == 0 → keep current state
				}

				// Final cavity
				var finalLen = h - curStart;
				if (finalLen > bestLen) { bestLen = finalLen; bestStart = curStart; }

				if (bestLen <= 1) return (0, 0);

				var cavEnd = bestStart + bestLen;

				// Within the cavity, scan from bottom: last row where dist is still growing
				var bottomBorder = bestStart;
				for (var y = bestStart + 1; y < cavEnd; y++)
				{
					if (dist[y] > dist[y - 1])
						bottomBorder = y;
					else if (dist[y] < dist[y - 1])
						break;
				}

				// Within the cavity, scan from top: last row where dist is still growing (from top)
				var topRow = cavEnd - 1;
				for (var y = cavEnd - 2; y >= bestStart; y--)
				{
					if (dist[y] > dist[y + 1])
						topRow = y;
					else if (dist[y] < dist[y + 1])
						break;
				}
				var topBorder = h - 1 - topRow;

				return (bottomBorder, topBorder);
			}

			var distA = ComputeEdgeDistances(0, borderA, threshA, -1);
			var distB = ComputeEdgeDistances(borderA, borderA + borderB, threshB, 1);

			var (bottomA, topA) = FindCavity(distA);
			var (bottomB, topB) = FindCavity(distB);

			return (bottomA, bottomB, topA, topB);
		}

		public static void LogDistanceProfile(SliceAnalysis analysis, int edgeThreshold = 0)
		{
			if (analysis.CollapsedPixels == null) return;

			var pixels = analysis.CollapsedPixels;
			var w = analysis.CollapsedWidth;
			var h = analysis.CollapsedHeight;
			var borderA = analysis.XDominant ? analysis.XAxis.BorderStart : analysis.YAxis.BorderStart;
			var borderB = analysis.XDominant ? analysis.XAxis.BorderEnd : analysis.YAxis.BorderEnd;

			var threshA = edgeThreshold >= 0 ? edgeThreshold : AutoEdgeThreshold(pixels, w, h, analysis.MaxAlpha, 0, borderA);
			var threshB = edgeThreshold >= 0 ? edgeThreshold : AutoEdgeThreshold(pixels, w, h, analysis.MaxAlpha, borderA, borderA + borderB);

			var sb = new System.Text.StringBuilder();
			sb.AppendLine($"Distance Profile (h={h}, borderA={borderA}, borderB={borderB}, threshA={threshA}, threshB={threshB}):");

			for (var y = 0; y < h; y++)
			{
				var distA = 0;
				for (var x = borderA - 2; x >= 0; x--)
				{
					if (LumDiff(pixels, w, y, x, analysis.MaxAlpha) > threshA)
					{ distA = borderA - 1 - x; break; }
				}

				var distB = 0;
				for (var x = borderA; x < borderA + borderB - 1; x++)
				{
					if (LumDiff(pixels, w, y, x, analysis.MaxAlpha) > threshB)
					{ distB = x - borderA + 1; break; }
				}

				var barA = new string('█', distA);
				var barB = new string('█', distB);
				sb.AppendLine($"  y={y,3}: A={distA,3} {barA}  |  B={distB,3} {barB}");
			}

			UnityEngine.Debug.Log(sb.ToString());
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
			private Color32[] _pixels;
			private int _width, _height;
			private byte _maxAlpha;

			public Analyzer(Texture2D texture, int margin)
			{
				_texture = texture;
				_margin = margin;
			}

			public SliceAnalysis Run()
			{
				_width = _texture.width;
				_height = _texture.height;
				_pixels = _texture.GetPixels32();

				_maxAlpha = 0;
				for (var i = 0; i < _pixels.Length; i++)
					if (_pixels[i].a > _maxAlpha) _maxAlpha = _pixels[i].a;

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

				// Pre-pass: if all 4 corner pixels are fully opaque → no rounded corners → bail
				if (_pixels[0].a >= _maxAlpha &&
				    _pixels[_width - 1].a >= _maxAlpha &&
				    _pixels[(_height - 1) * _width].a >= _maxAlpha &&
				    _pixels[(_height - 1) * _width + _width - 1].a >= _maxAlpha)
					return analysis;

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
				{
					analysis.CollapsedTexture = CreateTexture(_pixels, _width, _height);
					analysis.LuminanceTexture = CreateLuminanceTexture(_pixels, _width, _height);
					analysis.CollapsedPixels = (Color32[])_pixels.Clone();
					analysis.CollapsedWidth = _width;
					analysis.CollapsedHeight = _height;
					analysis.MaxAlpha = _maxAlpha;
				}

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

			private void FindSecondAxisBorders(SliceAnalysis result, int borderA, int borderB, bool xDominant)
			{
				var scanLength = xDominant ? _height : _width;

				// Pass 1: Alpha shape — scan outer edge per row
				var alphaStartA = AlphaScanHeight(0, borderA, 0, 1, -1);
				var alphaStartB = AlphaScanHeight(borderA, borderA + borderB, 0, 1, 1);
				var alphaEndA = AlphaScanHeight(0, borderA, scanLength - 1, -1, -1);
				var alphaEndB = AlphaScanHeight(borderA, borderA + borderB, scanLength - 1, -1, 1);

				// Pass 2: Inner edge — luminance derivative scan
				var (edgeStartA, edgeStartB, edgeEndA, edgeEndB) = EdgeScanBorders(result, 200);

				// Store debug
				result.AlphaStartA = alphaStartA;
				result.AlphaStartB = alphaStartB;
				result.AlphaEndA = alphaEndA;
				result.AlphaEndB = alphaEndB;
				result.EdgeStartA = edgeStartA;
				result.EdgeStartB = edgeStartB;
				result.EdgeEndA = edgeEndA;
				result.EdgeEndB = edgeEndB;

				// Combine: max of both passes
				result.HeightStartA = Mathf.Max(alphaStartA, edgeStartA);
				result.HeightStartB = Mathf.Max(alphaStartB, edgeStartB);
				result.HeightEndA = Mathf.Max(alphaEndA, edgeEndA);
				result.HeightEndB = Mathf.Max(alphaEndB, edgeEndB);

				var secondStart = Mathf.Max(result.HeightStartA, result.HeightStartB);
				var secondEnd = Mathf.Max(result.HeightEndA, result.HeightEndB);

				// Clamp if top + bottom overlap
				if (secondStart + secondEnd > scanLength)
				{
					secondStart = scanLength / 2;
					secondEnd = scanLength / 2;
				}

				if (xDominant)
					result.FinalBorder = new Border(borderA, secondStart, borderB, secondEnd);
				else
					result.FinalBorder = new Border(secondStart, borderA, secondEnd, borderB);
			}

			/// <summary>
			/// Pass 1: Alpha scan on the outer edge.
			/// For each row, find the outermost fg pixel. Track the row whose fg pixel
			/// is closest to the image edge. Among ties, pick the shallowest row.
			/// scanOutward: -1 for left side, +1 for right side.
			/// </summary>
			private int AlphaScanHeight(int colStart, int colEnd, int startRow, int dir, int scanOutward)
			{
				var edgeCol = scanOutward < 0 ? colStart : colEnd - 1;
				var bestCol = scanOutward < 0 ? colEnd : colStart - 1; // worst possible
				var bestH = 0;

				for (var i = 0; i < _height; i++)
				{
					var row = startRow + i * dir;
					if (row < 0 || row >= _height) break;

					var foundCol = -1;
					if (scanOutward < 0)
					{
						for (var col = colStart; col < colEnd; col++)
						{
							if (_pixels[row * _width + col].a >= _maxAlpha)
							{ foundCol = col; break; }
						}
						if (foundCol >= 0 && foundCol < bestCol)
						{
							bestCol = foundCol;
							bestH = i + 1;
							if (bestCol == edgeCol) return i == 0 ? 0 : bestH;
						}
					}
					else
					{
						for (var col = colEnd - 1; col >= colStart; col--)
						{
							if (_pixels[row * _width + col].a >= _maxAlpha)
							{ foundCol = col; break; }
						}
						if (foundCol >= 0 && foundCol > bestCol)
						{
							bestCol = foundCol;
							bestH = i + 1;
							if (bestCol == edgeCol) return i == 0 ? 0 : bestH;
						}
					}
				}

				return bestH;
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

			private static Texture2D CreateLuminanceTexture(Color32[] pixels, int width, int height)
			{
				var lumPixels = new Color32[pixels.Length];
				for (var i = 0; i < pixels.Length; i++)
				{
					var p = pixels[i];
					var lum = (byte)((299 * p.r + 587 * p.g + 114 * p.b) / 1000);
					lumPixels[i] = new Color32(lum, lum, lum, p.a);
				}

				var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
				tex.SetPixels32(lumPixels);
				tex.Apply();
				return tex;
			}
		}
	}
}
