
#region ================== Namespaces

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CodeImp.DoomBuilder.Config;
using CodeImp.DoomBuilder.IO;
using CodeImp.DoomBuilder.Map;

#endregion

namespace CodeImp.DoomBuilder.Data
{
	//
	// Doom 64 texture animations, defined by the ANIMDEFS lump, the same way the Doom 64 ports (Doom64EX) run them:
	//
	//   animpic "NAME"
	//   {
	//       restartdelay = 15   (extra tics to hold the last frame)
	//       frames = 4          (number of frames)
	//       speed = 7           (tics between frames)
	//       rewind              (play forward and backward, instead of starting over)
	//       cyclepalettes       (the frames are palettes of the texture, instead of the following textures)
	//   }
	//
	// A frame based animation shows the texture with the index of NAME plus the frame number (the textures follow each
	// other in the order of the game's texture list). A palette cycling animation shows the texture itself with the
	// palette that has the frame number: the palette lump PAL<first 4 letters of NAME><frame> when it exists, or else
	// the 16 colors at 16 * frame in the palette of the texture image.
	//
	// Only the ANIMDEFS of the resource with the highest priority is used: when a PWAD has an ANIMDEFS lump it replaces
	// the one of the IWAD completely, like it does in the game.
	// The animations run at 30 tics per second. The editor does not change the map, this only changes which image is drawn.
	//
	internal sealed class TextureAnimations : IDisposable
	{
		#region ================== Constants

		// Maximum number of tics that is simulated in one update. When the editor was not
		// updating for a longer time (window minimized, etc.) the animations just continue from here.
		private const int MAX_CATCHUP_TICS = 120;

		// Number of colors in a palette of a Doom 64 texture
		private const int PALETTE_COLORS = 16;

		#endregion

		#region ================== Classes

		// One animpic block, with the state the game keeps for it (animinfo_t)
		private sealed class AnimDef
		{
			public string name;
			public long longname;
			public int delay;
			public int frames;
			public int speed;
			public bool reverse;
			public bool palette;

			// State
			public bool isreverse;
			public int delayleft;
			public long nexttic;
			public int frame = -1;
			public int current = 0;		// The frame that is shown now

			// Port of the loop body of P_CyclePicAnims
			public void Step(long leveltime)
			{
				if(delayleft > 0)
				{
					delayleft--;
					return;
				}

				if(leveltime <= nexttic) return;

				int lastpic = isreverse ? 0 : (frames - 1);
				nexttic = leveltime + speed;
				frame += isreverse ? -1 : 1;
				current = Math.Max(0, Math.Min(frames - 1, frame));

				if(frame == lastpic)
				{
					if(delay > 0) delayleft = delay;

					if(reverse)
						isreverse = !isreverse;
					else
						frame = -1;
				}
			}
		}

		#endregion

		#region ================== Variables

		private readonly Dictionary<long, AnimDef> defs = new Dictionary<long, AnimDef>();
		private readonly List<AnimDef> order = new List<AnimDef>();

		// The images of the frames, by the image that is animated
		private readonly Dictionary<ImageData, ImageData[]> frameimages = new Dictionary<ImageData, ImageData[]>();
		private readonly List<ImageData> ownedimages = new List<ImageData>();

		// The game's texture list (index <-> name)
		private readonly Dictionary<string, int> indexbyname = new Dictionary<string, int>();
		private readonly Dictionary<int, string> namebyindex = new Dictionary<int, string>();

		private long leveltime = 0;
		private long lasttic = -1;
		private string source = null;
		private bool isdisposed = false;

		#endregion

		#region ================== Properties

		// The number of animations that are defined
		public int Count { get { return order.Count; } }

		// The resource that the animations were loaded from (or null)
		public string Source { get { return source; } }

		#endregion

		#region ================== Loading

		// This loads the animations from the resource with the highest priority that has an ANIMDEFS lump
		public void Load(List<DataReader> containers, List<TextureIndexInfo> textureindex)
		{
			defs.Clear();
			order.Clear();
			source = null;

			// The texture list of the game
			indexbyname.Clear();
			namebyindex.Clear();
			if(textureindex != null)
			{
				foreach(TextureIndexInfo ti in textureindex)
				{
					string title = ti.Title.ToUpperInvariant();
					if(!indexbyname.ContainsKey(title)) indexbyname.Add(title, ti.Index);
					if(!namebyindex.ContainsKey(ti.Index)) namebyindex.Add(ti.Index, title);
				}
			}

			// The last resource that has the lump wins, and the others are ignored completely
			for(int i = containers.Count - 1; i >= 0; i--)
			{
				Stream data = null;
				try { data = containers[i].GetLumpData("ANIMDEFS"); }
				catch(Exception e)
				{
					General.ErrorLogger.Add(ErrorType.Warning, "Cannot read ANIMDEFS from resource \"" + containers[i].Location.location + "\".\n" + e.GetType().Name + ": " + e.Message);
				}

				if(data != null)
				{
					string text;
					try
					{
						data.Seek(0, SeekOrigin.Begin);
						byte[] bytes = new byte[(int)data.Length];
						data.Read(bytes, 0, bytes.Length);
						text = Encoding.ASCII.GetString(bytes);
					}
					finally { data.Dispose(); }

					source = containers[i].Location.location;
					Parse(text);
					break;
				}
			}

			if(source != null)
				General.WriteLogLine("Loaded " + order.Count + " texture animations from ANIMDEFS in " + source);
		}

		// This splits the text in tokens
		private static List<string> Tokenize(string text)
		{
			List<string> tokens = new List<string>();
			int i = 0;
			while(i < text.Length)
			{
				char c = text[i];
				if(char.IsWhiteSpace(c))
				{
					i++;
				}
				else if((c == '/') && (i + 1 < text.Length) && (text[i + 1] == '/'))
				{
					while((i < text.Length) && (text[i] != '\n')) i++;
				}
				else if((c == '/') && (i + 1 < text.Length) && (text[i + 1] == '*'))
				{
					int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
					i = (end < 0) ? text.Length : end + 2;
				}
				else if(c == '"')
				{
					int end = text.IndexOf('"', i + 1);
					if(end < 0) end = text.Length;
					tokens.Add("\"" + text.Substring(i + 1, end - i - 1));
					i = end + 1;
				}
				else if((c == '{') || (c == '}') || (c == '='))
				{
					tokens.Add(c.ToString());
					i++;
				}
				else
				{
					int start = i;
					while((i < text.Length) && !char.IsWhiteSpace(text[i]) && (text[i] != '{') && (text[i] != '}') && (text[i] != '=') && (text[i] != '"')) i++;
					tokens.Add(text.Substring(start, i - start));
				}
			}
			return tokens;
		}

		// This reads the animpic blocks
		private void Parse(string text)
		{
			List<string> tokens = Tokenize(text);
			int pos = 0;

			while(pos < tokens.Count)
			{
				string token = tokens[pos++];
				if(!string.Equals(token, "animpic", StringComparison.OrdinalIgnoreCase))
				{
					General.ErrorLogger.Add(ErrorType.Warning, "Unexpected '" + token.TrimStart('"') + "' in ANIMDEFS. Expected 'animpic'.");
					continue;
				}

				if(pos >= tokens.Count) break;
				AnimDef d = new AnimDef();
				d.name = tokens[pos++].TrimStart('"').ToUpperInvariant();
				d.frames = 0;
				d.speed = 0;
				d.delay = 0;

				if((pos >= tokens.Count) || (tokens[pos] != "{"))
				{
					General.ErrorLogger.Add(ErrorType.Warning, "Animation '" + d.name + "' in ANIMDEFS does not have a '{'.");
					continue;
				}
				pos++;

				while((pos < tokens.Count) && (tokens[pos] != "}"))
				{
					string key = tokens[pos++].ToLowerInvariant();
					if(key == "rewind") { d.reverse = true; continue; }
					if(key == "cyclepalettes") { d.palette = true; continue; }

					// Name = value
					if((pos < tokens.Count) && (tokens[pos] == "=")) pos++;
					int value = 0;
					if((pos < tokens.Count) && (tokens[pos] != "}"))
					{
						int.TryParse(tokens[pos], out value);
						pos++;
					}

					switch(key)
					{
						case "restartdelay": d.delay = value; break;
						case "frames": d.frames = value; break;
						case "speed": d.speed = value; break;
						default:
							General.ErrorLogger.Add(ErrorType.Warning, "Unknown property '" + key + "' in animation '" + d.name + "' in ANIMDEFS.");
							break;
					}
				}
				if(pos < tokens.Count) pos++;	// The '}'

				// Needs at least 2 frames to animate. Frame based animations need the texture to be in the texture list.
				if(d.frames < 2) continue;
				if(!d.palette && !indexbyname.ContainsKey(d.name)) continue;

				d.longname = Lump.MakeLongName(d.name);

				// A later block for the same texture replaces the earlier one, like the last one wins in the game
				AnimDef old;
				if(defs.TryGetValue(d.longname, out old)) order.Remove(old);
				defs[d.longname] = d;
				order.Add(d);
			}
		}

		#endregion

		#region ================== Running

		// This advances the animations to the current time. Call this once per rendered frame.
		public void Update()
		{
			if(order.Count == 0) return;

			long tic = TextureScroll.GetCurrentTic();
			if(lasttic < 0) lasttic = tic;
			long steps = tic - lasttic;
			if(steps <= 0) return;
			lasttic = tic;
			if(steps > MAX_CATCHUP_TICS) steps = MAX_CATCHUP_TICS;

			for(long s = 0; s < steps; s++)
			{
				leveltime++;
				foreach(AnimDef d in order) d.Step(leveltime);
			}
		}

		// This returns true when the animations can be shown (Doom 64 map format only)
		private static bool CanAnimate()
		{
			return (General.Map != null) && (General.Map.FormatInterface != null) && General.Map.FormatInterface.InDoom64Mode;
		}

		// This returns true when the image is animated by the ANIMDEFS
		public bool IsAnimated(ImageData img)
		{
			return (img != null) && (order.Count > 0) && defs.ContainsKey(img.LongName) && CanAnimate();
		}

		// This returns the image to draw now for an image: the current frame of the animation when it is animated
		// and that frame is loaded, otherwise the image itself.
		public ImageData Translate(ImageData img)
		{
			AnimDef d;
			if((img == null) || (order.Count == 0) || !defs.TryGetValue(img.LongName, out d) || !CanAnimate()) return img;

			// This also starts loading the images of the frames, so they are ready when they are needed
			ImageData[] frames = GetFrames(d, img);
			if((frames == null) || (d.current <= 0)) return img;

			ImageData f = frames[Math.Min(d.current, frames.Length - 1)];
			if((f != null) && !f.IsDisposed && f.IsImageLoaded && !f.LoadFailed) return f;
			return img;
		}

		// This finds or makes the images of all the frames of an animation
		private ImageData[] GetFrames(AnimDef d, ImageData img)
		{
			ImageData[] frames;
			if(frameimages.TryGetValue(img, out frames)) return frames;

			frames = new ImageData[d.frames];
			frames[0] = img;

			if(d.palette)
			{
				// The same texture with other palettes
				for(int i = 1; i < d.frames; i++)
				{
					PaletteImage pi = new PaletteImage(img, i);
					pi.AddReference();
					ownedimages.Add(pi);
					frames[i] = pi;
				}
			}
			else
			{
				// The textures that follow in the texture list, of the same kind (texture or flat) as this image
				int baseindex = indexbyname[d.name];
				bool isflat = General.Map.Data.GetFlatExists(img.LongName) && object.ReferenceEquals(General.Map.Data.GetFlatImage(img.LongName), img);
				for(int i = 1; i < d.frames; i++)
				{
					string name;
					if(!namebyindex.TryGetValue(baseindex + i, out name) || (name == "-")) continue;

					long ln = Lump.MakeLongName(name);
					ImageData target = null;
					if(isflat)
					{
						if(General.Map.Data.GetFlatExists(ln)) target = General.Map.Data.GetFlatImage(ln);
					}
					else
					{
						if(General.Map.Data.GetTextureExists(ln)) target = General.Map.Data.GetTextureImage(ln);
					}

					// Make sure the image is loaded
					if(target != null) target.AddReference();
					frames[i] = target;
				}
			}

			frameimages[img] = frames;
			return frames;
		}

		#endregion

		#region ================== Disposing

		public void Dispose()
		{
			if(!isdisposed)
			{
				foreach(ImageData img in ownedimages) img.Dispose();
				ownedimages.Clear();
				frameimages.Clear();
				defs.Clear();
				order.Clear();
				isdisposed = true;
			}
		}

		#endregion
	}

	//
	// A Doom 64 texture drawn with one of its other palettes (for the animations that cycle palettes).
	//
	internal sealed class PaletteImage : ImageData
	{
		#region ================== Variables

		private readonly int paletteframe;
		private static uint[] crctable;

		#endregion

		#region ================== Constructor / Disposer

		// Constructor
		public PaletteImage(ImageData baseimage, int paletteframe)
		{
			this.paletteframe = paletteframe;
			this.scale = baseimage.Scale;

			// The name is the one of the texture, so that a finished load updates the map where that texture is used
			SetName(baseimage.Name);

			// We have no destructor
			GC.SuppressFinalize(this);
		}

		#endregion

		#region ================== Methods

		// This calculates the CRC of a PNG chunk
		private static uint PngCrc(byte[] data, int offset, int length)
		{
			if(crctable == null)
			{
				uint[] t = new uint[256];
				for(uint n = 0; n < 256; n++)
				{
					uint c = n;
					for(int k = 0; k < 8; k++)
						c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
					t[n] = c;
				}
				crctable = t;
			}

			uint crc = 0xFFFFFFFFu;
			for(int i = offset; i < offset + length; i++)
				crc = crctable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
			return crc ^ 0xFFFFFFFFu;
		}

		// This reads all bytes of a stream
		private static byte[] ReadAll(Stream s)
		{
			s.Seek(0, SeekOrigin.Begin);
			byte[] bytes = new byte[(int)s.Length];
			s.Read(bytes, 0, bytes.Length);
			return bytes;
		}

		// This makes the first colors of the palette of the PNG the colors of another palette (in place).
		// A Doom 64 texture is a 4 bit PNG that has the palettes one after the other in its PLTE chunk.
		private bool ApplyPalette(byte[] png)
		{
			if((png.Length < 20) || (png[0] != 0x89) || (png[1] != 0x50) || (png[2] != 0x4E) || (png[3] != 0x47)) return false;

			int pos = 8;
			while(pos + 12 <= png.Length)
			{
				int len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
				if((len < 0) || (pos + 12 + len > png.Length)) return false;

				if((png[pos + 4] == 'P') && (png[pos + 5] == 'L') && (png[pos + 6] == 'T') && (png[pos + 7] == 'E'))
				{
					int count = Math.Min(len / 3, PALETTE_COLORS_PER_PALETTE);
					byte[] colors = null;

					// A palette lump (PAL + first 4 letters of the name + number) has the colors for this palette
					if(Name.Length >= 4)
					{
						Stream pl = General.Map.Data.GetLumpData("PAL" + Name.Substring(0, 4).ToUpperInvariant() + paletteframe.ToString());
						if(pl != null)
						{
							try { colors = ReadAll(pl); }
							finally { pl.Dispose(); }
							if(colors.Length < (count * 3)) colors = null;
						}
					}

					// Otherwise the colors are in the PLTE chunk, after the colors of the previous palettes
					if(colors == null)
					{
						int first = paletteframe * PALETTE_COLORS_PER_PALETTE;
						if((first + count) * 3 > len) return false;
						colors = new byte[count * 3];
						Array.Copy(png, pos + 8 + (first * 3), colors, 0, count * 3);
					}

					Array.Copy(colors, 0, png, pos + 8, count * 3);

					uint crc = PngCrc(png, pos + 4, len + 4);
					png[pos + 8 + len] = (byte)(crc >> 24);
					png[pos + 9 + len] = (byte)(crc >> 16);
					png[pos + 10 + len] = (byte)(crc >> 8);
					png[pos + 11 + len] = (byte)crc;
					return true;
				}

				// PLTE must come before the image data
				if((png[pos + 4] == 'I') && (png[pos + 5] == 'D') && (png[pos + 6] == 'A') && (png[pos + 7] == 'T')) return false;

				pos += 12 + len;
			}

			return false;
		}

		private const int PALETTE_COLORS_PER_PALETTE = 16;

		// This loads the image
		protected override void LocalLoadImage()
		{
			// Leave when already loaded
			if(this.IsImageLoaded) return;

			lock(this)
			{
				if(bitmap != null) bitmap.Dispose();
				bitmap = null;

				// The data of the texture (a texture can be in the lists of textures and of flats)
				Stream lumpdata = General.Map.Data.GetFlatData(Name);
				if(lumpdata == null) lumpdata = General.Map.Data.GetTextureData(Name);

				if(lumpdata != null)
				{
					// NOTE: Do not dispose this stream, it is the stream of the lump in the resource and
					// other images read from it too
					byte[] bytes = ReadAll(lumpdata);

					if(ApplyPalette(bytes))
					{
						MemoryStream mem = new MemoryStream(bytes);
						IImageReader reader = ImageDataFormat.GetImageReader(mem);
						if(!(reader is UnknownImageReader))
						{
							mem.Seek(0, SeekOrigin.Begin);
							try { bitmap = reader.ReadAsBitmap(mem); }
							catch(InvalidDataException) { bitmap = null; }
						}
						mem.Dispose();
					}
				}

				if(bitmap == null)
				{
					loadfailed = true;
				}
				else
				{
					width = bitmap.Size.Width;
					height = bitmap.Size.Height;
				}

				// Pass on to base
				base.LocalLoadImage();
			}
		}

		#endregion
	}
}
