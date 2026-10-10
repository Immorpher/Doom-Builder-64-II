
#region ================== Copyright (c) 2007 Pascal vd Heiden

/*
 * Copyright (c) 2007 Pascal vd Heiden, www.codeimp.com
 * This program is released under GNU General Public License
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 */

#endregion

#region ================== Namespaces

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using CodeImp.DoomBuilder.IO;
using System.Windows.Forms;
using SlimDX.Direct3D9;
using CodeImp.DoomBuilder.Config;
using System.Threading;
using CodeImp.DoomBuilder.Map;
using CodeImp.DoomBuilder.Windows;

#endregion

namespace CodeImp.DoomBuilder.Data
{
	public sealed class DataManager
	{
		#region ================== Constants
		
		public const string INTERNAL_PREFIX = "internal:";
		
		#endregion

		#region ================== Variables
		
		// Data containers
		private List<DataReader> containers;

		// Doom 64 texture animations (ANIMDEFS)
		private TextureAnimations animations;
		
		// Resource files as they were when the resources were loaded (to detect outside changes)
		private List<DataLocation> resourcelocations = new List<DataLocation>();
		private Dictionary<string, string> resourcestamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		
        // villsa - thing palettes
        private Dictionary<string, Playpal> thingpalettes;
		
		// Textures, Flats and Sprites
		private Dictionary<long, ImageData> textures;
		private List<string> texturenames;
		private Dictionary<long, ImageData> flats;
		private List<string> flatnames;
		private Dictionary<long, ImageData> sprites;
        private Dictionary<string, ImageData> palettesprites; // styd: separate cache for sprites loaded with a non-default palette (palindex > 0) — see GetSpriteImage(name, palindex)
        private List<MatchingTextureSet> texturesets;
		private List<ResourceTextureSet> resourcetextures;
		private AllTextureSet alltextures;
		
		// Background loading
		private Queue<ImageData> imageque;
		private Thread backgroundloader;
		private volatile bool updatedusedtextures;
		private bool notifiedbusy;
		
		// Image previews
		private PreviewManager previews;
		
		// Special images
        private ImageData thingcamera;  // villsa 9/11/11
        private ImageData thingtrigger; // villsa 9/11/11
		private ImageData missingtexture3d;
		private ImageData unknowntexture3d;
		private ImageData hourglass3d;
		private ImageData crosshair;
		private ImageData crosshairbusy;
		private Dictionary<string, ImageData> internalsprites;
		private ImageData thingbox;
		private ImageData whitetexture;
		
		// Used images
		private Dictionary<long, long> usedimages;
		
		private List<ThingCategory> thingcategories;
		private Dictionary<int, ThingTypeInfo> thingtypes;
		
		// Timing
		private double loadstarttime;
		private double loadfinishtime;
		
		// Disposing
		private bool isdisposed = false;

		#endregion

		#region ================== Properties

        public IDictionary<string, Playpal> ThingPalette { get { return thingpalettes; } } // villsa
		public PreviewManager Previews { get { return previews; } }

		// The Doom 64 texture animations (ANIMDEFS)
		internal TextureAnimations Animations { get { return animations; } }
		public ICollection<ImageData> Textures { get { return textures.Values; } }
		public ICollection<ImageData> Flats { get { return flats.Values; } }
		public List<string> TextureNames { get { return texturenames; } }
		public List<string> FlatNames { get { return flatnames; } }
		public bool IsDisposed { get { return isdisposed; } }
        public ImageData ThingCamera { get { return thingcamera; } }    // villsa 9/11/11
        public ImageData ThingTrigger { get { return thingtrigger; } }  // villsa 9/11/11
		public ImageData MissingTexture3D { get { return missingtexture3d; } }
		public ImageData UnknownTexture3D { get { return unknowntexture3d; } }
		public ImageData Hourglass3D { get { return hourglass3d; } }
		public ImageData Crosshair3D { get { return crosshair; } }
		public ImageData CrosshairBusy3D { get { return crosshairbusy; } }
		public ImageData ThingBox { get { return thingbox; } }
		public ImageData WhiteTexture { get { return whitetexture; } }
		public List<ThingCategory> ThingCategories { get { return thingcategories; } }
		public ICollection<ThingTypeInfo> ThingTypes { get { return thingtypes.Values; } }
		internal ICollection<MatchingTextureSet> TextureSets { get { return texturesets; } }
		internal ICollection<ResourceTextureSet> ResourceTextureSets { get { return resourcetextures; } }
		internal AllTextureSet AllTextureSet { get { return alltextures; } }
		
		public bool IsLoading
		{
			get
			{
				if(imageque != null)
				{
					return (backgroundloader != null) && backgroundloader.IsAlive && ((imageque.Count > 0) || previews.IsLoading);
				}
				else
				{
					return false;
				}
			}
		}
		
		#endregion

		#region ================== Constructor / Disposer

		// Constructor
		internal DataManager()
		{
			// We have no destructor
			GC.SuppressFinalize(this);

			// Load special images
			missingtexture3d = new ResourceImage("CodeImp.DoomBuilder.Resources.MissingTexture3D.png");
			missingtexture3d.LoadImage();
			unknowntexture3d = new ResourceImage("CodeImp.DoomBuilder.Resources.UnknownTexture3D.png");
			unknowntexture3d.LoadImage();
			hourglass3d = new ResourceImage("CodeImp.DoomBuilder.Resources.Hourglass3D.png");
			hourglass3d.LoadImage();
            thingcamera = new ResourceImage("CodeImp.DoomBuilder.Resources.ThingCamera.png");   // villsa 9/11/11
            thingcamera.LoadImage();    // villsa 9/11/11
            thingtrigger = new ResourceImage("CodeImp.DoomBuilder.Resources.ThingTrigger.png");   // villsa 9/11/11
            thingtrigger.LoadImage();    // villsa 9/11/11
			crosshair = new ResourceImage("CodeImp.DoomBuilder.Resources.Crosshair.png");
			crosshair.LoadImage();
			crosshairbusy = new ResourceImage("CodeImp.DoomBuilder.Resources.CrosshairBusy.png");
			crosshairbusy.LoadImage();
			thingbox = new ResourceImage("CodeImp.DoomBuilder.Resources.ThingBox.png");
			thingbox.LoadImage();
			whitetexture = new ResourceImage("CodeImp.DoomBuilder.Resources.White.png");
			whitetexture.UseColorCorrection = false;
			whitetexture.LoadImage();
			whitetexture.CreateTexture();
		}
		
		// Disposer
		internal void Dispose()
		{
			// Not already disposed?
			if(!isdisposed)
			{
				// Clean up
				Unload();
                thingcamera.Dispose();  // villsa 9/11/11
                thingcamera = null; // villsa 9/11/11
                thingtrigger.Dispose(); // villsa 9/11/11
                thingtrigger = null;    // villsa 9/11/11
				missingtexture3d.Dispose();
				missingtexture3d = null;
				unknowntexture3d.Dispose();
				unknowntexture3d = null;
				hourglass3d.Dispose();
				hourglass3d = null;
				crosshair.Dispose();
				crosshair = null;
				crosshairbusy.Dispose();
				crosshairbusy = null;
				thingbox.Dispose();
				thingbox = null;
				whitetexture.Dispose();
				whitetexture = null;
				
				// Done
				isdisposed = true;
			}
		}

		#endregion

		#region ================== Loading / Unloading

		// This loads all data resources
		internal void Load(DataLocationList configlist, DataLocationList maplist, DataLocation maplocation)
		{
			DataLocationList all = DataLocationList.Combined(configlist, maplist);
			all.Add(maplocation);
			Load(all);
		}

		// This loads all data resources
		internal void Load(DataLocationList configlist, DataLocationList maplist)
		{
			DataLocationList all = DataLocationList.Combined(configlist, maplist);
			Load(all);
		}

		// This loads all data resources
		internal void Load(DataLocationList locations)
		{
			int texcount, flatcount, spritecount, colormapcount;
			Dictionary<long, ImageData> texturesonly = new Dictionary<long, ImageData>();
			Dictionary<long, ImageData> colormapsonly = new Dictionary<long, ImageData>();
			Dictionary<long, ImageData> flatsonly = new Dictionary<long, ImageData>();
			DataReader c;
			
			// Remember the state of the resource files as they are right now
			resourcelocations = new List<DataLocation>(locations);
			UpdateResourceStamps();
			
			// Create collections
			containers = new List<DataReader>();
			textures = new Dictionary<long, ImageData>();
            thingpalettes = new Dictionary<string, Playpal>();
			flats = new Dictionary<long, ImageData>();
			sprites = new Dictionary<long, ImageData>();
            palettesprites = new Dictionary<string, ImageData>();
            texturenames = new List<string>();
			flatnames = new List<string>();
			imageque = new Queue<ImageData>();
			previews = new PreviewManager();
			texturesets = new List<MatchingTextureSet>();
			usedimages = new Dictionary<long, long>();
			internalsprites = new Dictionary<string, ImageData>();
			thingcategories = General.Map.Config.GetThingCategories();
			thingtypes = General.Map.Config.GetThingTypes();
			
			// Load texture sets
			foreach(DefinedTextureSet ts in General.Map.ConfigSettings.TextureSets)
				texturesets.Add(new MatchingTextureSet(ts));
			
			// Sort the texture sets
			texturesets.Sort();
			
			// Special textures sets
			alltextures = new AllTextureSet();
			resourcetextures = new List<ResourceTextureSet>();
			
			// Go for all locations
			foreach(DataLocation dl in locations)
			{
				// Nothing chosen yet
				c = null;

				// TODO: Make this work more elegant using reflection.
				// Make DataLocation.type of type Type and assign the
				// types of the desired reader classes.

				try
				{
					// Choose container type
					switch(dl.type)
					{
						// WAD file container
						case DataLocation.RESOURCE_WAD:
							c = new WADReader(dl);
							break;

						// Directory container
						case DataLocation.RESOURCE_DIRECTORY:
							c = new DirectoryReader(dl);
							break;

						// PK3 file container
						case DataLocation.RESOURCE_PK3:
							c = new PK3Reader(dl);
							break;
					}
				}
				catch(Exception e)
				{
					// Unable to load resource
					General.ErrorLogger.Add(ErrorType.Error, "Unable to load resources from location \"" + dl.location + "\". Please make sure the location is accessible and not in use by another program. The resources will now be loaded with this location excluded. You may reload the resources to try again.\n" + e.GetType().Name + " when creating data reader: " + e.Message + ")");
					General.WriteLogLine(e.StackTrace);
					continue;
				}	

				// Add container
				if(c != null)
				{
					containers.Add(c);
					resourcetextures.Add(c.TextureSet);
				}
			}
			
			// Load stuff
			texcount = LoadTextures(texturesonly);
			flatcount = LoadFlats(flatsonly);
			colormapcount = LoadColormaps(colormapsonly);
            // Thing palettes must be available before any palette-swapped sprite is created/loaded
            foreach (TextureIndexInfo tp in General.Map.Config.ThingPalettes)
            {
                LoadThingPalette(tp.Title);
            }

            LoadSprites();
            spritecount = LoadThingSprites();
            LoadInternalSprites();
			
			// Process colormaps (we just put them in as textures)
			foreach(KeyValuePair<long, ImageData> t in colormapsonly)
			{
				textures.Add(t.Key, t.Value);
				texturenames.Add(t.Value.Name);
			}
			
			// Process textures
			foreach(KeyValuePair<long, ImageData> t in texturesonly)
			{
				if(!textures.ContainsKey(t.Key))
				{
					textures.Add(t.Key, t.Value);
					texturenames.Add(t.Value.Name);
				}
			}

			// Process flats
			foreach(KeyValuePair<long, ImageData> f in flatsonly)
			{
				flats.Add(f.Key, f.Value);
				flatnames.Add(f.Value.Name);
			}

			// Mixed textures and flats?
			if(General.Map.Config.MixTexturesFlats)
			{
				// Add textures to flats
				foreach(KeyValuePair<long, ImageData> t in texturesonly)
				{
					if(!flats.ContainsKey(t.Key))
					{
						flats.Add(t.Key, t.Value);
						flatnames.Add(t.Value.Name);
					}
				}

				// Add flats to textures
				foreach(KeyValuePair<long, ImageData> f in flatsonly)
				{
					if(!textures.ContainsKey(f.Key))
					{
						textures.Add(f.Key, f.Value);
						texturenames.Add(f.Value.Name);
					}
				}

				// Do the same on the data readers
				foreach(DataReader dr in containers)
					dr.TextureSet.MixTexturesAndFlats();
			}
			
			// Sort names
			texturenames.Sort();
			flatnames.Sort();

			// Sort things
			foreach(ThingCategory tc in thingcategories) tc.SortIfNeeded();

			// Load the Doom 64 texture animations (ANIMDEFS of the resource with the highest priority)
			if(animations != null) animations.Dispose();
			animations = new TextureAnimations();
			animations.Load(containers, General.Map.Config.D64TextureIndex);

			// Update the used textures
			General.Map.Data.UpdateUsedTextures();
			
			// Add texture names to texture sets
			foreach(KeyValuePair<long, ImageData> img in textures)
			{
				// Add to all sets where it matches
				bool matchfound = false;
				foreach(MatchingTextureSet ms in texturesets)
					matchfound |= ms.AddTexture(img.Value);

				// Add to all
				alltextures.AddTexture(img.Value);
			}
			
			// Add flat names to texture sets
			foreach(KeyValuePair<long, ImageData> img in flats)
			{
				// Add to all sets where it matches
				bool matchfound = false;
				foreach(MatchingTextureSet ms in texturesets)
					matchfound |= ms.AddFlat(img.Value);
				
				// Add to all
				alltextures.AddFlat(img.Value);
			}
			
			// Start background loading
			StartBackgroundLoader();
			
			// Output info
			General.WriteLogLine("Loaded " + texcount + " textures, " + flatcount + " flats, " + colormapcount + " colormaps, " + spritecount + " sprites");
		}
		
		// This unloads all data
		internal void Unload()
		{
			// Stop background loader
			StopBackgroundLoader();
			
			// Dispose preview manager
			previews.Dispose();
			previews = null;

			// Dispose the texture animations (they own images that were made from the textures)
			if(animations != null) animations.Dispose();
			animations = null;

            // Dispose resources
            foreach(KeyValuePair<long, ImageData> i in textures) i.Value.Dispose();
			foreach(KeyValuePair<long, ImageData> i in flats) i.Value.Dispose();
			foreach(KeyValuePair<long, ImageData> i in sprites) i.Value.Dispose();
			foreach(KeyValuePair<string, ImageData> i in palettesprites) i.Value.Dispose();
			
			// Dispose containers
			foreach(DataReader c in containers) c.Dispose();
			containers.Clear();

            // Trash collections
            containers = null;
			textures = null;
            thingpalettes = null;   // villsa
			flats = null;
			sprites = null;
			palettesprites = null;
			texturenames = null;
			flatnames = null;
			imageque = null;
			internalsprites = null;
		}
		
		#endregion
		
		#region ================== Suspend / Resume

		// This creates a stamp (last write time and size) that identifies the current state of a resource
		private static string GetResourceStamp(DataLocation dl)
		{
			try
			{
				if(dl.type == DataLocation.RESOURCE_DIRECTORY)
				{
					if(!Directory.Exists(dl.location)) return "missing";
					return "dir " + Directory.GetLastWriteTimeUtc(dl.location).Ticks.ToString(CultureInfo.InvariantCulture);
				}
				else
				{
					FileInfo fi = new FileInfo(dl.location);
					if(!fi.Exists) return "missing";
					return "file " + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + " " + fi.Length.ToString(CultureInfo.InvariantCulture);
				}
			}
			catch(Exception)
			{
				return "unknown";
			}
		}
		
		// This remembers the current state of all resource files, so that
		// only changes made from now on are reported by GetChangedResources
		internal void UpdateResourceStamps()
		{
			resourcestamps.Clear();
			foreach(DataLocation dl in resourcelocations)
				resourcestamps[dl.location] = GetResourceStamp(dl);
		}
		
		// This returns the names of the resources that were changed (or removed) since they were loaded.
		// This only looks at the file information, so it is very cheap to call.
		internal List<string> GetChangedResources()
		{
			List<string> changed = new List<string>();
			foreach(DataLocation dl in resourcelocations)
			{
				string oldstamp;
				if(resourcestamps.TryGetValue(dl.location, out oldstamp) && (oldstamp != GetResourceStamp(dl)))
					changed.Add(Path.GetFileName(dl.location.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
			}
			return changed;
		}
		
		// This suspends data resources
		internal void Suspend()
		{
			// Stop background loader
			StopBackgroundLoader();
			
			// Go for all containers
			foreach(DataReader d in containers)
			{
				// Suspend
				General.WriteLogLine("Suspended data resource '" + d.Location.location + "'");
				d.Suspend();
			}
		}

		// This resumes data resources
		internal void Resume()
		{
			// Go for all containers
			foreach(DataReader d in containers)
			{
				try
				{
					// Resume
					General.WriteLogLine("Resumed data resource '" + d.Location.location + "'");
					d.Resume();
				}
				catch(Exception e)
				{
					// Unable to load resource
					General.ErrorLogger.Add(ErrorType.Error, "Unable to load resources from location \"" + d.Location.location + "\". Please make sure the location is accessible and not in use by another program. The resources will now be loaded with this location excluded. You may reload the resources to try again.\n" + e.GetType().Name + " when resuming data reader: " + e.Message + ")");
					General.WriteLogLine(e.StackTrace);
				}
			}
			
			// Start background loading
			StartBackgroundLoader();
		}
		
		#endregion

		#region ================== Background Loading
		
		// This starts background loading
		private void StartBackgroundLoader()
		{
			// Timing
			loadstarttime = General.stopwatch.Elapsed.TotalMilliseconds;
			loadfinishtime = 0;
			
			// If a loader is already running, stop it first
			if(backgroundloader != null) StopBackgroundLoader();

			// Start a low priority thread to load images in background
			General.WriteLogLine("Starting background resource loading...");
			backgroundloader = new Thread(new ThreadStart(BackgroundLoad));
			backgroundloader.Name = "Background Loader";
			backgroundloader.Priority = ThreadPriority.Lowest;
			backgroundloader.IsBackground = true;
			backgroundloader.Start();
		}
		
		// This stops background loading
		private void StopBackgroundLoader()
		{
			ImageData img;
			
			General.WriteLogLine("Stopping background resource loading...");
			if(backgroundloader != null)
			{
				// Stop the thread and wait for it to end
				backgroundloader.Interrupt();
				backgroundloader.Join();

				// Reset load states on all images in the list
				while(imageque.Count > 0)
				{
					img = imageque.Dequeue();
					
					switch(img.ImageState)
					{
						case ImageLoadState.Loading:
							img.ImageState = ImageLoadState.None;
							break;

						case ImageLoadState.Unloading:
							img.ImageState = ImageLoadState.Ready;
							break;
					}

					switch(img.PreviewState)
					{
						case ImageLoadState.Loading:
							img.PreviewState = ImageLoadState.None;
							break;

						case ImageLoadState.Unloading:
							img.PreviewState = ImageLoadState.Ready;
							break;
					}
				}
				
				// Done
				notifiedbusy = false;
				backgroundloader = null;
				General.SendMessage(General.MainWindow.Handle, (int)MainForm.ThreadMessages.UpdateStatus, 0, 0);
			}
		}
		
		// The background loader
		private void BackgroundLoad()
		{
			try
			{
				do
				{
					// Do we have to update the used-in-map status?
					if(updatedusedtextures) BackgroundUpdateUsedTextures();
					
					// Get next item
					ImageData image = null;
					lock(imageque)
					{
						// Fetch next image to process
						if(imageque.Count > 0) image = imageque.Dequeue();
					}
					
					// Any image to process?
					if(image != null)
					{
						// Load this image?
						if(image.IsReferenced && (image.ImageState != ImageLoadState.Ready))
						{
							image.LoadImage();
						}
						
						// Unload this image?
						if(!image.IsReferenced && image.AllowUnload && (image.ImageState != ImageLoadState.None))
						{
							// Still unreferenced?
							image.UnloadImage();
						}
					}
					
					// Doing something?
					if(image != null)
					{
						// Wait a bit and update icon
						if(!notifiedbusy)
						{
							notifiedbusy = true;
							General.SendMessage(General.MainWindow.Handle, (int)MainForm.ThreadMessages.UpdateStatus, 0, 0);
						}
						Thread.Sleep(0);
					}
					else
					{
						// Process previews only when we don't have images to process
						// because these are lower priority than the actual images
						if(previews.BackgroundLoad())
						{
							// Wait a bit and update icon
							if(!notifiedbusy)
							{
								notifiedbusy = true;
								General.SendMessage(General.MainWindow.Handle, (int)MainForm.ThreadMessages.UpdateStatus, 0, 0);
							}
							Thread.Sleep(0);
						}
						else
						{
							if(notifiedbusy)
							{
								notifiedbusy = false;
								General.SendMessage(General.MainWindow.Handle, (int)MainForm.ThreadMessages.UpdateStatus, 0, 0);
							}
							
							// Timing
							if(loadfinishtime == 0)
							{
								loadfinishtime = General.stopwatch.Elapsed.TotalMilliseconds;
								double deltatimesec = (loadfinishtime - loadstarttime) / 1000.0d;
								General.WriteLogLine("Resources loading took " + deltatimesec.ToString("########0.00") + " seconds");
							}
							
							// Wait longer to release CPU resources
							Thread.Sleep(50);
						}
					}
				}
				while(true);
			}
			catch(ThreadInterruptedException)
			{
				return;
			}
		}
		
		// This adds an image for background loading or unloading
		internal void ProcessImage(ImageData img)
		{
			// Load this image?
			if((img.ImageState == ImageLoadState.None) && img.IsReferenced)
			{
				// Add for loading
				img.ImageState = ImageLoadState.Loading;
				lock(imageque) { imageque.Enqueue(img); }
			}
			
			// Unload this image?
			if((img.ImageState == ImageLoadState.Ready) && !img.IsReferenced && img.AllowUnload)
			{
				// Add for unloading
				img.ImageState = ImageLoadState.Unloading;
				lock(imageque) { imageque.Enqueue(img); }
			}
			
			// Update icon
			General.SendMessage(General.MainWindow.Handle, (int)MainForm.ThreadMessages.UpdateStatus, 0, 0);
		}

		// This updates the used-in-map status on all textures and flats
		private void BackgroundUpdateUsedTextures()
		{
			lock(usedimages)
			{
				// Set used on all textures
				foreach(KeyValuePair<long, ImageData> i in textures)
				{
					i.Value.SetUsedInMap(usedimages.ContainsKey(i.Key));
					if(i.Value.IsImageLoaded != i.Value.IsReferenced) ProcessImage(i.Value);
				}

				// Set used on all flats
				foreach(KeyValuePair<long, ImageData> i in flats)
				{
					i.Value.SetUsedInMap(usedimages.ContainsKey(i.Key));
					if(i.Value.IsImageLoaded != i.Value.IsReferenced) ProcessImage(i.Value);
				}
				
				// Done
				updatedusedtextures = false;
			}
		}
		
		#endregion
		
		#region ================== Thing Palettes

        // villsa
        private void LoadThingPalette(string name)
        {
            Playpal pal;
            // Go for all opened containers
            for (int i = containers.Count - 1; i >= 0; i--)
            {
                // Load palette
                pal = containers[i].LoadThingPalette(name);
                if (pal != null)
                {
                    thingpalettes[name] = pal;
                    return;
                }
            }

            // styd: make this failure visible — without this warning, a missing palette lump
            // (e.g. the game configuration's IWAD/resource isn't loaded, or the lump name is wrong)
            // silently falls back to the default palette with zero indication why.
            General.ErrorLogger.Add(ErrorType.Warning, "Could not find thing palette lump '" + name + "' in any loaded resource. Monster palette variants using this palette will not display correctly.");
        }

        // Returns the alternate thing palette registered under the given palette index
        // in the game configuration's "thingpalettes" section (or null when not loaded).
        public Playpal GetThingPaletteByIndex(int palindex)
        {
            if (thingpalettes == null || General.Map == null) return null;

            foreach (TextureIndexInfo tp in General.Map.Config.ThingPalettes)
            {
                if (tp.Index == palindex && thingpalettes.ContainsKey(tp.Title))
                    return thingpalettes[tp.Title];
            }

            return null;
        }

        // styd: on-demand palette loader for a sprite's OWN base palette (e.g. PALTROO0 for any
        // TROO* sprite), which isn't pre-registered in the "thingpalettes" config section (only the
        // alternate/variant palettes like PALTROO1 are). Needed as the reverse-lookup key when
        // remapping a PNG sprite's baked-in colors to an alternate palette — see ImageData.LocalLoadImage.
        public Playpal GetOrLoadThingPalette(string name)
        {
            if (thingpalettes.ContainsKey(name))
                return thingpalettes[name];

            for (int i = containers.Count - 1; i >= 0; i--)
            {
                Playpal pal = containers[i].LoadThingPalette(name);
                if (pal != null)
                {
                    thingpalettes[name] = pal;
                    return pal;
                }
            }

            return null;
        }

        #endregion

        #region ================== Colormaps

        // This loads the colormaps
        private int LoadColormaps(Dictionary<long, ImageData> list)
		{
			ICollection<ImageData> images;
			int counter = 0;

			// Go for all opened containers
			foreach(DataReader dr in containers)
			{
				// Load colormaps
				images = dr.LoadColormaps();
				if(images != null)
				{
					// Go for all colormaps
					foreach(ImageData img in images)
					{
						// Add or replace in flats list
						list.Remove(img.LongName);
						list.Add(img.LongName, img);
						counter++;

						// Add to preview manager
						previews.AddImage(img);
					}
				}
			}

			// Output info
			return counter;
		}

		// This returns a specific colormap stream
		internal Stream GetColormapData(string pname)
		{
			Stream colormap;

			// Go for all opened containers
			for(int i = containers.Count - 1; i >= 0; i--)
			{
				// This contain provides this flat?
				colormap = containers[i].GetColormapData(pname);
				if(colormap != null) return colormap;
			}

			// No such patch found
			return null;
		}

		#endregion

		#region ================== Textures

		// This loads the textures
		private int LoadTextures(Dictionary<long, ImageData> list)
		{
			ICollection<ImageData> images;
			PatchNames pnames = new PatchNames();
			PatchNames newpnames;
			int counter = 0;
			long firsttexture = 0;

			// Go for all opened containers
			foreach(DataReader dr in containers)
			{
				// Load PNAMES info
				// Note that pnames is NOT set to null in the loop
				// because if a container has no pnames, the pnames
				// of the previous (higher) container should be used.
				newpnames = dr.LoadPatchNames();
				if(newpnames != null) pnames = newpnames;

				// Load textures
				images = dr.LoadTextures(pnames);
				if(images != null)
				{
					// Go for all textures
					foreach(ImageData img in images)
					{
						// Add or replace in textures list
						list.Remove(img.LongName);
						list.Add(img.LongName, img);
						if(firsttexture == 0) firsttexture = img.LongName;
						counter++;
						
						// Add to preview manager
						previews.AddImage(img);
					}
				}
			}
			
			// The first texture cannot be used, because in the game engine it
			// has index 0 which means "no texture", so remove it from the list.
			list.Remove(firsttexture);
			
			// Output info
			return counter;
		}
		
		// This returns a specific patch stream
		internal Stream GetPatchData(string pname)
		{
			Stream patch;

			// Go for all opened containers
			for(int i = containers.Count - 1; i >= 0; i--)
			{
				// This contain provides this patch?
				patch = containers[i].GetPatchData(pname);
				if(patch != null) return patch;
			}

			// No such patch found
			return null;
		}

		// This returns a specific texture stream
		internal Stream GetTextureData(string pname)
		{
			Stream patch;

			// Go for all opened containers
			for(int i = containers.Count - 1; i >= 0; i--)
			{
				// This contain provides this patch?
				patch = containers[i].GetTextureData(pname);
				if(patch != null) return patch;
			}

			// No such patch found
			return null;
		}
		
		// This checks if a given texture is known
		public bool GetTextureExists(string name)
		{
			long longname = Lump.MakeLongName(name);
			return textures.ContainsKey(longname);
		}
		
		// This checks if a given texture is known
		public bool GetTextureExists(long longname)
		{
			return textures.ContainsKey(longname);
		}
		
		// This returns an image by string
		public ImageData GetTextureImage(string name)
		{
			// Get the long name
			long longname = Lump.MakeLongName(name);
			return GetTextureImage(longname);
		}
		
		// This returns an image by long
		public ImageData GetTextureImage(long longname)
		{
			// Does this texture exist?
			if(textures.ContainsKey(longname))
			{
				// Return texture
				return textures[longname];
			}
			else
			{
				// Return null image
				return new UnknownImage(Properties.Resources.UnknownImage);
			}
		}
		
		#endregion

		#region ================== Flats

		// This loads the flats
		private int LoadFlats(Dictionary<long, ImageData> list)
		{
			ICollection<ImageData> images;
			int counter = 0;
			
			// Go for all opened containers
			foreach(DataReader dr in containers)
			{
				// Load flats
				images = dr.LoadFlats();
				if(images != null)
				{
					// Go for all flats
					foreach(ImageData img in images)
					{
						// Add or replace in flats list
						list.Remove(img.LongName);
						list.Add(img.LongName, img);
						counter++;

						// Add to preview manager
						previews.AddImage(img);
					}
				}
			}

			// Output info
			return counter;
		}

		// This returns the data of a lump by name from the resource with the highest priority that has it
		internal Stream GetLumpData(string name)
		{
			for(int i = containers.Count - 1; i >= 0; i--)
			{
				Stream data = containers[i].GetLumpData(name);
				if(data != null) return data;
			}
			return null;
		}

		// This returns a specific flat stream
		internal Stream GetFlatData(string pname)
		{
			Stream flat;

			// Go for all opened containers
			for(int i = containers.Count - 1; i >= 0; i--)
			{
				// This contain provides this flat?
				flat = containers[i].GetFlatData(pname);
				if(flat != null) return flat;
			}

			// No such patch found
			return null;
		}

		// This checks if a flat is known
		public bool GetFlatExists(string name)
		{
			long longname = Lump.MakeLongName(name);
			return flats.ContainsKey(longname);
		}

		// This checks if a flat is known
		public bool GetFlatExists(long longname)
		{
			return flats.ContainsKey(longname);
		}
		
		// This returns an image by string
		public ImageData GetFlatImage(string name)
		{
			// Get the long name
			long longname = Lump.MakeLongName(name);
			return GetFlatImage(longname);
		}

		// This returns an image by long
		public ImageData GetFlatImage(long longname)
		{
			// Does this flat exist?
			if(flats.ContainsKey(longname))
			{
				// Return flat
				return flats[longname];
			}
			else
			{
				// Return null image
				return new UnknownImage(Properties.Resources.UnknownImage);
			}
		}

		// This returns an image by long and doesn't check if it exists
		public ImageData GetFlatImageKnown(long longname)
		{
			// Return flat
			return flats[longname];
		}

        #endregion

        #region ================== Sprites

        // This loads the hard defined sprites (not all the lumps, we do that on a need-to-know basis, see LoadThingSprites)
        private int LoadSprites()
        {
            ICollection<ImageData> images;
            int counter = 0;

            // Load all defined sprites. Note that we do not use all sprites,
            // so we don't add them for previews just yet.
            foreach(DataReader dr in containers)
            {
                // Load sprites
                images = dr.LoadSprites();
                if(images != null)
                {
                    // Add or replace in sprites list
                    foreach(ImageData img in images)
                    {
                        sprites[img.LongName] = img;
                        counter++;
                    }
                }
            }

            // Output info
            return counter;
        }

        // This loads the sprites that we really need for things
        private int LoadThingSprites()
        {
            // Gather the names of all sprites, so that the rotations of each thing sprite can be found
            List<string> spritenames = new List<string>();
            foreach(DataReader dr in containers)
            {
                IEnumerable<string> names = dr.GetSpriteNames();
                if(names != null) spritenames.AddRange(names);
            }

            // Go for all things
            foreach(ThingTypeInfo ti in General.Map.Data.ThingTypes)
            {
                // Valid sprite name?
                if((ti.Sprite.Length > 0) && (ti.Sprite.Length <= 8))
                {
                    // Find all rotations of the sprite
                    ti.SetupSpriteFrame(spritenames);

                    // The sprite used for previews
                    ImageData image = LoadThingSpriteImage(ti.Sprite, ti.SpriteLongName, ti.PalIndex);

                    // Add to preview manager
                    if(image != null)
                        previews.AddImage(image);

                    // Make images for all the rotations
                    SpriteFrameInfo[] frames = ti.SpriteFrame;
                    if(frames.Length > 1)
                    {
                        foreach(SpriteFrameInfo frame in frames)
                            LoadThingSpriteImage(frame.Sprite, frame.SpriteLongName, ti.PalIndex);
                    }
                }
            }

            // Output info
            return sprites.Count;
        }

        // This makes (or finds) the image of a thing sprite
        private ImageData LoadThingSpriteImage(string spritename, long spritelongname, int palindex)
        {
            ImageData image = null;

            // styd: use the palette-aware lookup — thing types sharing the same sprite name
            // (e.g. Imp / Nightmare Imp both use "TROOA2A8") but a different PalIndex must
            // NOT share one cached ImageData instance, or only the first-loaded type's palette
            // ever gets applied.
            if (palindex > 0)
            {
                image = GetSpriteImage(spritename, palindex);
            }
            else if (!sprites.ContainsKey(spritelongname))
            {
                // Find sprite data
                Stream spritedata = GetSpriteData(spritename);
                if(spritedata != null)
                {
                    // Make new sprite image
                    image = new SpriteImage(spritename);

                    // Add to collection
                    sprites.Add(spritelongname, image);
                }
            }
            else
            {
                image = sprites[spritelongname];
            }

            return image;
        }

        // This returns a specific patch stream
        internal Stream GetSpriteData(string pname)
		{
			if(!string.IsNullOrEmpty(pname))
			{
				// Go for all opened containers
				for(int i = containers.Count - 1; i >= 0; i--)
				{
					// This contain provides this patch?
					Stream spritedata = containers[i].GetSpriteData(pname);
					if(spritedata != null) return spritedata;
				}
			}
			
			// No such patch found
			return null;
		}

		// This tests if a given sprite can be found
		internal bool GetSpriteExists(string pname)
		{
			if(!string.IsNullOrEmpty(pname))
			{
				long longname = Lump.MakeLongName(pname);
				if(sprites.ContainsKey(longname))
					return true;

				// Go for all opened containers
				for(int i = containers.Count - 1; i >= 0; i--)
				{
					// This contain provides this patch?
					if(containers[i].GetSpriteExists(pname)) return true;
				}
			}
			
			// No such patch found
			return false;
		}
		
		// This loads the internal sprites
		private void LoadInternalSprites()
		{
			// Add sprite icon files from directory
			string[] files = Directory.GetFiles(General.SpritesPath, "*.png", SearchOption.TopDirectoryOnly);
			foreach(string spritefile in files)
			{
				ImageData img = new FileImage(Path.GetFileNameWithoutExtension(spritefile).ToLowerInvariant(), spritefile, false);
				img.LoadImage();
				img.AllowUnload = false;
				internalsprites.Add(img.Name, img);
			}
			
			// Add some internal resources
			if(!internalsprites.ContainsKey("nothing"))
			{
				ImageData img = new ResourceImage("CodeImp.DoomBuilder.Resources.Nothing.png");
				img.LoadImage();
				img.AllowUnload = false;
				internalsprites.Add("nothing", img);
			}
			
			if(!internalsprites.ContainsKey("unknownthing"))
			{
				ImageData img = new ResourceImage("CodeImp.DoomBuilder.Resources.UnknownThing.png");
				img.LoadImage();
				img.AllowUnload = false;
				internalsprites.Add("unknownthing", img);
			}
		}
		
		// This returns an image by long
		public ImageData GetSpriteImage(string name)
		{
			// Is this referring to an internal sprite image?
			if((name.Length > INTERNAL_PREFIX.Length) && name.ToLowerInvariant().StartsWith(INTERNAL_PREFIX))
			{
				// Get the internal sprite
				string internalname = name.Substring(INTERNAL_PREFIX.Length).ToLowerInvariant();
				if(internalsprites.ContainsKey(internalname))
				{
					return internalsprites[internalname];
				}
				else
				{
					return new UnknownImage(Properties.Resources.UnknownImage);
				}
			}
			else
			{
				// Get the long name
				long longname = Lump.MakeLongName(name);

				// Sprite already loaded?
				if(sprites.ContainsKey(longname))
				{
					// Return exiting sprite
					return sprites[longname];
				}
				else
				{
					Stream spritedata = null;
					
					// Go for all opened containers
					for(int i = containers.Count - 1; i >= 0; i--)
					{
						// This contain provides this sprite?
						spritedata = containers[i].GetSpriteData(name);
						if(spritedata != null) break;
					}
					
					// Found anything?
					if(spritedata != null)
					{
						// Make new sprite image
						SpriteImage image = new SpriteImage(name);

						// Add to collection
						sprites.Add(longname, image);

						// Return result
						return image;
					}
					else
					{
						// Return null image
						return new UnknownImage(Properties.Resources.UnknownImage);
					}
				}
			}
		}

        // styd: overload that supports Doom 64 monster palette variants (e.g. Nightmare Imp
        // reuses sprite "TROOA2A8" — the same as the regular Imp — but needs palette #7 applied).
        // The plain GetSpriteImage(name) caches purely by sprite name, so two thing types sharing
        // a sprite name but different PalIndex would incorrectly share one ImageData instance and
        // only whichever type loaded first would ever get its palette applied. This overload gives
        // each distinct (name, palindex) pair its own cached, independently palette-remapped instance.
        public ImageData GetSpriteImage(string name, int palindex)
        {
            // No palette variant requested — behaves exactly like the original method
            if (palindex <= 0)
                return GetSpriteImage(name);

            // Internal sprites (camera, trigger markers, etc.) never have Doom 64 palette variants
            if ((name.Length > INTERNAL_PREFIX.Length) && name.ToLowerInvariant().StartsWith(INTERNAL_PREFIX))
                return GetSpriteImage(name);

            string cachekey = name.Trim().ToUpperInvariant() + "#" + palindex;

            if (palettesprites.ContainsKey(cachekey))
            {
                return palettesprites[cachekey];
            }
            else
            {
                Stream spritedata = null;

                // Go for all opened containers
                for (int i = containers.Count - 1; i >= 0; i--)
                {
                    spritedata = containers[i].GetSpriteData(name);
                    if (spritedata != null) break;
                }

                if (spritedata != null)
                {
                    SpriteImage image = new SpriteImage(name);
                    image.PalIndex = palindex;

                    palettesprites.Add(cachekey, image);

                    return image;
                }
                else
                {
                    return new UnknownImage(Properties.Resources.UnknownImage);
                }
            }
        }

        #endregion

        #region ================== Things

		// This gets thing information by index
		public ThingTypeInfo GetThingInfo(int thingtype)
		{
			// Index in config?
			if(thingtypes.ContainsKey(thingtype))
			{
				// Return from config
				return thingtypes[thingtype];
			}
			else
			{
				// Create unknown thing info
				return new ThingTypeInfo(thingtype);
			}
		}

		// This gets thing information by index
		// Returns null when thing type info could not be found
		public ThingTypeInfo GetThingInfoEx(int thingtype)
		{
			// Index in config?
			if(thingtypes.ContainsKey(thingtype))
			{
				// Return from config
				return thingtypes[thingtype];
			}
			else
			{
				// No such thing type known
				return null;
			}
		}
		
		#endregion
		
		#region ================== Tools

		// This finds the first IWAD resource
		// Returns false when not found
		internal bool FindFirstIWAD(out DataLocation result)
		{
			// Go for all data containers
			foreach(DataReader dr in containers)
			{
				// Container is a WAD file?
				if(dr is WADReader)
				{
					// Check if it is an IWAD
					WADReader wr = dr as WADReader;
					if(wr.IsIWAD)
					{
						// Return location!
						result = wr.Location;
						return true;
					}
				}
			}

			// No IWAD found
			result = new DataLocation();
			return false;
		}

		// This signals the background thread to update the
		// used-in-map status on all textures and flats
		public void UpdateUsedTextures()
		{
			lock(usedimages)
			{
				usedimages.Clear();

				// Go through the map to find the used textures
				foreach(Sidedef sd in General.Map.Map.Sidedefs)
				{
					// Add used textures to dictionary
					if(sd.HighTexture.Length > 0) usedimages[sd.LongHighTexture] = 0;
					if(sd.LowTexture.Length > 0) usedimages[sd.LongLowTexture] = 0;
					if(sd.MiddleTexture.Length > 0) usedimages[sd.LongMiddleTexture] = 0;
				}

				// Go through the map to find the used flats
				foreach(Sector s in General.Map.Map.Sectors)
				{
					// Add used flats to dictionary
					usedimages[s.LongFloorTexture] = 0;
					usedimages[s.LongCeilTexture] = 0;

					// The bottom layer of a Doom 64 liquid floor is the flat after the floor flat,
					// which no sector uses itself, but it must be loaded to be drawn in 3D mode
					if(TextureScroll.IsLiquid(s))
					{
						string liquidbase = TextureScroll.GetLiquidBaseName(s);
						if(liquidbase != null) usedimages[Lump.MakeLongName(liquidbase)] = 0;
					}
				}
				
				// Notify the background thread that it needs to update the images
				updatedusedtextures = true;
			}
		}

		// This returns the long name for a string
		public long GetLongImageName(string name)
		{
			return Lump.MakeLongName(name);
		}
		
		#endregion
	}
}
