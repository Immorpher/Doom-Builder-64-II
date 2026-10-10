
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
using System.Windows.Forms;
using System.IO;
using System.Reflection;
using System.Drawing;
using System.ComponentModel;
using CodeImp.DoomBuilder.Map;
using SlimDX.Direct3D9;
using SlimDX;
using CodeImp.DoomBuilder.Geometry;
using System.Drawing.Imaging;
using CodeImp.DoomBuilder.Data;
using CodeImp.DoomBuilder.Editing;

using Configuration = CodeImp.DoomBuilder.IO.Configuration;

#endregion

namespace CodeImp.DoomBuilder.Rendering
{
	internal class SurfaceManager : ID3DResource
	{
		#region ================== Constants
		
		// The true maximum lies at 65535 if I remember correctly, but that
		// is a scary big number for a vertexbuffer.
		private const int MAX_VERTICES_PER_BUFFER = 30000;
		
		// When a sector exceeds this number of vertices, it should split up it's triangles
		// This number must be a multiple of 3.
		public const int MAX_VERTICES_PER_SECTOR = 6000;
		
		#endregion
		
		#region ================== Variables
		
		// Set of buffers for a specific number of vertices per sector
		private Dictionary<int, SurfaceBufferSet> sets;
		
		// List of buffers that are locked
		// This is null when not in the process of updating
		private List<VertexBuffer> lockedbuffers;
		
		// Surface to be rendered.
		// Each BinaryHeap in the Dictionary contains all geometry that needs
		// to be rendered with the associated ImageData.
		// The BinaryHeap sorts the geometry by sector to minimize stream switchs.
		// This is null when not in the process of rendering
		private Dictionary<ImageData, List<SurfaceEntry>> surfaces;
		
		// This is 1 to add the number of vertices to the offset
		// (effectively rendering the ceiling vertices instead of floor vertices)
		private int surfacevertexoffsetmul;
		
		// Entries that change over time in the game: flats that scroll (Doom 64 scroll and liquid sector flags)
		// and sectors with a light effect. These are drawn separately, with a texture coordinate offset and glow.
		private List<SurfaceEntry> scrollentries = new List<SurfaceEntry>();
		private bool scrollceiling;
		private bool scrolltextured;

		// True when a flat that was drawn is animated by the ANIMDEFS (Doom 64 texture animations)
		private bool animatedtextures;
		
		// This is set to true when the resources have been unloaded
		private bool resourcesunloaded;

		#endregion

		#region ================== Properties

		// True when the last render included sectors with moving flats or light effects (so the 2D view has to keep redrawing)
		public bool HasAnimatedSurfaces { get { return (scrollentries.Count > 0) || animatedtextures; } }

		#endregion

		#region ================== Constructor / Disposer
		
		// Constructor
		public SurfaceManager()
		{
			sets = new Dictionary<int, SurfaceBufferSet>();
			lockedbuffers = new List<VertexBuffer>();

			General.Map.Graphics.RegisterResource(this);
		}
		
		// Disposer
		public void Dispose()
		{
			if(sets != null)
			{
				General.Map.Graphics.UnregisterResource(this);
				
				// Dispose all sets
				foreach(KeyValuePair<int, SurfaceBufferSet> set in sets)
				{
					// Dispose vertex buffers
					for(int i = 0; i < set.Value.buffers.Count; i++)
					{
						if(set.Value.buffers[i] != null)
						{
							set.Value.buffers[i].Dispose();
							set.Value.buffers[i] = null;
						}
					}
				}
				
				sets = null;
			}
		}
		
		#endregion

		#region ================== Management

		// Called when all resource must be unloaded
		public void UnloadResource()
		{
			resourcesunloaded = true;
			foreach(KeyValuePair<int, SurfaceBufferSet> set in sets)
			{
				// Dispose vertex buffers
				for(int i = 0; i < set.Value.buffers.Count; i++)
				{
					if(set.Value.buffers[i] != null)
					{
						set.Value.buffers[i].Dispose();
						set.Value.buffers[i] = null;
					}
				}
			}
			
			lockedbuffers.Clear();
		}

		// Called when all resource must be reloaded
		public void ReloadResource()
		{
			foreach(KeyValuePair<int, SurfaceBufferSet> set in sets)
			{
				// Rebuild vertex buffers
				for(int i = 0; i < set.Value.buffersizes.Count; i++)
				{
					// Make the new buffer!
					VertexBuffer b = new VertexBuffer(General.Map.Graphics.Device, FlatVertex.Stride * set.Value.buffersizes[i],
													Usage.WriteOnly | Usage.Dynamic, VertexFormat.None, Pool.Default);

					// Start refilling the buffer with sector geometry
					DataStream bstream = b.Lock(0, FlatVertex.Stride * set.Value.buffersizes[i], LockFlags.Discard);
					foreach(SurfaceEntry e in set.Value.entries)
					{
						if(e.bufferindex == i)
						{
							// Fill buffer
							bstream.Seek(e.vertexoffset * FlatVertex.Stride, SeekOrigin.Begin);
							bstream.WriteRange(e.floorvertices);
							bstream.WriteRange(e.ceilvertices);
						}
					}

					// Unlock buffer
					b.Unlock();
					bstream.Dispose();
					
					// Add to list
					set.Value.buffers[i] = b;
				}
			}
			
			resourcesunloaded = false;
		}
		
		// This resets all buffers and requires all sectors to get new entries
		public void Reset()
		{
			scrollentries.Clear();
			
			// Clear all items
			foreach(KeyValuePair<int, SurfaceBufferSet> set in sets)
			{
				foreach(SurfaceEntry entry in set.Value.entries)
				{
					entry.numvertices = -1;
					entry.bufferindex = -1;
				}
				
				foreach(SurfaceEntry entry in set.Value.holes)
				{
					entry.numvertices = -1;
					entry.bufferindex = -1;
				}

				foreach(VertexBuffer vb in set.Value.buffers)
					vb.Dispose();
			}

			// New dictionary
			sets = new Dictionary<int, SurfaceBufferSet>();
		}

		// Updating sector surface geometry should go in this order;
		// - Triangulate sectors
		// - Call FreeSurfaces to remove entries that have changed number of vertices
		// - Call AllocateBuffers
		// - Call UpdateSurfaces to add/update entries
		// - Call UnlockBuffers
		
		// This (re)allocates the buffers based on an analysis of the map
		// The map must be updated (triangulated) before calling this
		public void AllocateBuffers()
		{
			// Make analysis of sector geometry
			Dictionary<int, int> sectorverts = new Dictionary<int, int>();
			foreach(Sector s in General.Map.Map.Sectors)
			{
				if(s.Triangles != null)
				{
					int numvertices = s.Triangles.Vertices.Count;
					while(numvertices > 0)
					{
						// Determine for how many vertices in this entry
						int vertsinentry = (numvertices > MAX_VERTICES_PER_SECTOR) ? MAX_VERTICES_PER_SECTOR : numvertices;
						
						// We count the number of sectors that have specific number of vertices
						if(!sectorverts.ContainsKey(vertsinentry))
							sectorverts.Add(vertsinentry, 0);
						sectorverts[vertsinentry]++;

						numvertices -= vertsinentry;
					}
				}
			}
			
			// Now (re)allocate the needed buffers
			foreach(KeyValuePair<int, int> sv in sectorverts)
			{
				// Zero vertices can't be drawn
				if(sv.Key > 0)
				{
					SurfaceBufferSet set = GetSet(sv.Key);
					
					// Calculte how many free entries we need
					int neededentries = sv.Value;
					int freeentriesneeded = neededentries - set.entries.Count;

					// Allocate the space needed
					EnsureFreeBufferSpace(set, freeentriesneeded);
				}
			}
		}

		// This ensures there is enough space for a given number of free entries (also adds new bufers if needed)
		private void EnsureFreeBufferSpace(SurfaceBufferSet set, int freeentries)
		{
			DataStream bstream = null;
			VertexBuffer vb = null;
			
			// Check if we have to add entries
			int addentries = freeentries - set.holes.Count;

			// Begin resizing buffers starting with the last in this set
			int bufferindex = set.buffers.Count - 1;

			// Calculate the maximum number of entries we can put in a new buffer
			// Note that verticesperentry is the number of vertices multiplied by 2, because
			// we have to store both the floor and ceiling
			int verticesperentry = set.numvertices * 2;
			int maxentriesperbuffer = MAX_VERTICES_PER_BUFFER / verticesperentry;

			// Make a new bufer when the last one is full
			if((bufferindex > -1) && (set.buffersizes[bufferindex] >= (maxentriesperbuffer * verticesperentry)))
				bufferindex = -1;
			
			while(addentries > 0)
			{
				// Create a new buffer?
				if((bufferindex == -1) || (bufferindex > (set.buffers.Count - 1)))
				{
					// Determine the number of entries we will be making this buffer for
					int bufferentries = (addentries > maxentriesperbuffer) ? maxentriesperbuffer : addentries;

					// Calculate the number of vertices that will be
					int buffernumvertices = bufferentries * verticesperentry;

					if(!resourcesunloaded)
					{
						// Make the new buffer!
						vb = new VertexBuffer(General.Map.Graphics.Device, FlatVertex.Stride * buffernumvertices,
												Usage.WriteOnly | Usage.Dynamic, VertexFormat.None, Pool.Default);

						// Add it.
						set.buffers.Add(vb);
					}
					else
					{
						// We can't make a vertexbuffer right now
						set.buffers.Add(null);
					}
					
					// Also add available entries as holes, because they are not used yet.
					set.buffersizes.Add(buffernumvertices);
					for(int i = 0; i < bufferentries; i++)
						set.holes.Add(new SurfaceEntry(set.numvertices, set.buffers.Count - 1, i * verticesperentry));

					// Done
					addentries -= bufferentries;
				}
				// Reallocate a buffer
				else
				{
					// Trash the old buffer
					if(set.buffers[bufferindex].Tag != null)
					{
						bstream = (DataStream)set.buffers[bufferindex].Tag;
						set.buffers[bufferindex].Unlock();
						bstream.Dispose();
						set.buffers[bufferindex].Tag = null;
					}

					if((set.buffers[bufferindex] != null) && !resourcesunloaded)
						set.buffers[bufferindex].Dispose();

					// Get the entries that are in this buffer only
					List<SurfaceEntry> theseentries = new List<SurfaceEntry>();
					foreach(SurfaceEntry e in set.entries)
					{
						if(e.bufferindex == bufferindex)
							theseentries.Add(e);
					}

					// Determine the number of entries we will be making this buffer for
					int bufferentries = ((theseentries.Count + addentries) > maxentriesperbuffer) ? maxentriesperbuffer : (theseentries.Count + addentries);

					// Calculate the number of vertices that will be
					int buffernumvertices = bufferentries * verticesperentry;

					if(!resourcesunloaded)
					{
						// Make the new buffer and lock it
						vb = new VertexBuffer(General.Map.Graphics.Device, FlatVertex.Stride * buffernumvertices,
												Usage.WriteOnly | Usage.Dynamic, VertexFormat.None, Pool.Default);
						bstream = vb.Lock(0, FlatVertex.Stride * theseentries.Count * verticesperentry, LockFlags.Discard);
					}
					
					// Start refilling the buffer with sector geometry
					int vertexoffset = 0;
					foreach(SurfaceEntry e in theseentries)
					{
						if(!resourcesunloaded)
						{
							// Fill buffer
							bstream.WriteRange(e.floorvertices);
							bstream.WriteRange(e.ceilvertices);
						}

						// Set the new location in the buffer
						e.vertexoffset = vertexoffset;

						// Move on
						vertexoffset += verticesperentry;
					}

					if(!resourcesunloaded)
					{
						// Unlock buffer
						vb.Unlock();
						bstream.Dispose();
						set.buffers[bufferindex] = vb;
					}
					else
					{
						// No vertex buffer at this time, sorry
						set.buffers[bufferindex] = null;
					}

					// Set the new buffer and add available entries as holes, because they are not used yet.
					set.buffersizes[bufferindex] = buffernumvertices;
					set.holes.Clear();
					for(int i = 0; i < bufferentries - theseentries.Count; i++)
						set.holes.Add(new SurfaceEntry(set.numvertices, bufferindex, i * verticesperentry + vertexoffset));

					// Done
					addentries -= bufferentries;
				}

				// Always continue in next (new) buffer
				bufferindex = set.buffers.Count;
			}
		}
		
		// This adds or updates sector geometry into a buffer.
		// Modiies the list of SurfaceEntries with the new surface entry for the stored geometry.
		public void UpdateSurfaces(SurfaceEntryCollection entries, SurfaceUpdate update)
		{
			// Free entries when number of vertices has changed
			if((entries.Count > 0) && (entries.totalvertices != update.numvertices))
			{
				FreeSurfaces(entries);
				entries.Clear();
			}
			
			if((entries.Count == 0) && (update.numvertices > 0))
			{
				#if DEBUG
				if((update.floorvertices == null) || (update.ceilvertices == null))
					General.Fail("We need both floor and ceiling vertices when the number of vertices changes!");
				#endif
				
				// If we have no entries yet, we have to make them now
				int vertsremaining = update.numvertices;
				while(vertsremaining > 0)
				{
					// Determine for how many vertices in this entry
					int vertsinentry = (vertsremaining > MAX_VERTICES_PER_SECTOR) ? MAX_VERTICES_PER_SECTOR : vertsremaining;

					// Lookup the set that holds entries for this number of vertices
					SurfaceBufferSet set = GetSet(vertsinentry);

					// Make sure we can get a new entry in this set
					EnsureFreeBufferSpace(set, 1);

					// Get a new entry in this set
					SurfaceEntry e = set.holes[set.holes.Count - 1];
					set.holes.RemoveAt(set.holes.Count - 1);
					set.entries.Add(e);
					
					// Fill the entry data
					e.floorvertices = new FlatVertex[vertsinentry];
					e.ceilvertices = new FlatVertex[vertsinentry];
					Array.Copy(update.floorvertices, update.numvertices - vertsremaining, e.floorvertices, 0, vertsinentry);
					Array.Copy(update.ceilvertices, update.numvertices - vertsremaining, e.ceilvertices, 0, vertsinentry);
					e.floortexture = update.floortexture;
					e.ceiltexture = update.ceiltexture;
					
					entries.Add(e);
					vertsremaining -= vertsinentry;
				}
			}
			else
			{
				// We re-use the same entries, just copy over the updated data
				int vertsremaining = update.numvertices;
				foreach(SurfaceEntry e in entries)
				{
					if(update.floorvertices != null)
					{
						Array.Copy(update.floorvertices, update.numvertices - vertsremaining, e.floorvertices, 0, e.numvertices);
						e.floortexture = update.floortexture;
					}

					if(update.ceilvertices != null)
					{
						Array.Copy(update.ceilvertices, update.numvertices - vertsremaining, e.ceilvertices, 0, e.numvertices);
						e.ceiltexture = update.ceiltexture;
					}

					vertsremaining -= e.numvertices;
				}
			}

			entries.totalvertices = update.numvertices;
			
			// Time to update or create the buffers
			foreach(SurfaceEntry e in entries)
			{
				SurfaceBufferSet set = GetSet(e.numvertices);

				// Update bounding box
				e.UpdateBBox();
				
				if(!resourcesunloaded)
				{
					// Lock the buffer
					DataStream bstream;
					VertexBuffer vb = set.buffers[e.bufferindex];
					if(vb.Tag == null)
					{
						// Note: DirectX warns me that I am not using LockFlags.Discard or LockFlags.NoOverwrite here,
						// but we don't have much of a choice since we want to update our data and not destroy other data
						bstream = vb.Lock(0, set.buffersizes[e.bufferindex] * FlatVertex.Stride, LockFlags.None);
						vb.Tag = bstream;
						lockedbuffers.Add(vb);
					}
					else
					{
						bstream = (DataStream)vb.Tag;
					}

					// Write the vertices to buffer
					bstream.Seek(e.vertexoffset * FlatVertex.Stride, SeekOrigin.Begin);
					bstream.WriteRange(e.floorvertices);
					bstream.WriteRange(e.ceilvertices);
				}
			}
		}

		// This frees the given surface entry
		public void FreeSurfaces(SurfaceEntryCollection entries)
		{
			foreach(SurfaceEntry e in entries)
			{
				if((e.numvertices > 0) && (e.bufferindex > -1))
				{
					SurfaceBufferSet set = sets[e.numvertices];
					set.entries.Remove(e);
					SurfaceEntry newentry = new SurfaceEntry(e);
					set.holes.Add(newentry);
				}
				e.numvertices = -1;
				e.bufferindex = -1;
			}
		}
		
		// This unlocks the locked buffers
		public void UnlockBuffers()
		{
			if(!resourcesunloaded)
			{
				foreach(VertexBuffer vb in lockedbuffers)
				{
					if(vb.Tag != null)
					{
						DataStream bstream = (DataStream)vb.Tag;
						vb.Unlock();
						bstream.Dispose();
						vb.Tag = null;
					}
				}

				// Clear list
				lockedbuffers = new List<VertexBuffer>();
			}
		}
		
		// This gets or creates a set for a specific number of vertices
		private SurfaceBufferSet GetSet(int numvertices)
		{
			SurfaceBufferSet set;
			
			// Get or create the set
			if(!sets.ContainsKey(numvertices))
			{
				set = new SurfaceBufferSet();
				set.numvertices = numvertices;
				set.buffers = new List<VertexBuffer>();
				set.buffersizes = new List<int>();
				set.entries = new List<SurfaceEntry>();
				set.holes = new List<SurfaceEntry>();
				sets.Add(numvertices, set);
			}
			else
			{
				set = sets[numvertices];
			}

			return set;
		}
		
		#endregion
		
		#region ================== Rendering
		
		// This renders all sector floors
		internal void RenderSectorFloors(RectangleF viewport)
		{
			surfaces = new Dictionary<ImageData, List<SurfaceEntry>>();
			surfacevertexoffsetmul = 0;
			scrollentries.Clear();
			animatedtextures = false;
			scrollceiling = false;
			scrolltextured = true;
			
			// Go for all surfaces as they are sorted in the buffers, so that
			// they are automatically already sorted by vertexbuffer
			foreach(KeyValuePair<int, SurfaceBufferSet> set in sets)
			{
				foreach(SurfaceEntry entry in set.Value.entries)
				{
					if(entry.bbox.IntersectsWith(viewport))
					{
						if(IsAnimatedEntry(entry, false, true))
							scrollentries.Add(entry);
						else
							AddSurfaceEntryForRendering(entry, entry.floortexture);
					}
				}
			}
		}
		
		// This renders all sector ceilings
		internal void RenderSectorCeilings(RectangleF viewport)
		{
			surfaces = new Dictionary<ImageData, List<SurfaceEntry>>();
			surfacevertexoffsetmul = 1;
			scrollentries.Clear();
			animatedtextures = false;
			scrollceiling = true;
			scrolltextured = true;
			
			// Go for all surfaces as they are sorted in the buffers, so that
			// they are automatically already sorted by vertexbuffer
			foreach(KeyValuePair<int, SurfaceBufferSet> set in sets)
			{
				foreach(SurfaceEntry entry in set.Value.entries)
				{
					if(entry.bbox.IntersectsWith(viewport))
					{
						if(IsAnimatedEntry(entry, true, true))
							scrollentries.Add(entry);
						else
							AddSurfaceEntryForRendering(entry, entry.ceiltexture);
					}
				}
			}
		}

		// This renders all sector brightness levels
		internal void RenderSectorBrightness(RectangleF viewport)
		{
			surfaces = new Dictionary<ImageData, List<SurfaceEntry>>();
			surfacevertexoffsetmul = 0;
			scrollentries.Clear();
			animatedtextures = false;
			scrollceiling = false;
			scrolltextured = false;
			
			// Go for all surfaces as they are sorted in the buffers, so that
			// they are automatically already sorted by vertexbuffer
			foreach(KeyValuePair<int, SurfaceBufferSet> set in sets)
			{
				foreach(SurfaceEntry entry in set.Value.entries)
				{
					if(entry.bbox.IntersectsWith(viewport))
					{
						if(IsAnimatedEntry(entry, false, false))
							scrollentries.Add(entry);
						else
							AddSurfaceEntryForRendering(entry, 0);
					}
				}
			}
		}

		// This returns true when the entry changes over time in the game (Doom 64 only): the flat moves
		// (only when flats are shown) or the sector has a light effect
		private static bool IsAnimatedEntry(SurfaceEntry entry, bool ceiling, bool textured)
		{
			if((entry.sector == null) || entry.sector.IsDisposed) return false;
			return (textured && TextureScroll.IsScrolling(entry.sector, ceiling)) || SectorGlow.IsAnimated(entry.sector);
		}

		// This adds a surface entry to the list of surfaces
		private void AddSurfaceEntryForRendering(SurfaceEntry entry, long longimagename)
		{
			ImageData img = GetImageForRendering(longimagename);
			
			// Store by texture
			if(!surfaces.ContainsKey(img))
				surfaces.Add(img, new List<SurfaceEntry>());
			surfaces[img].Add(entry);
		}

		// This determines the image to draw for a flat (0 = white)
		private ImageData GetImageForRendering(long longimagename)
		{
			// Determine texture to use
			ImageData img;
			if(longimagename == 0)
			{
				img = General.Map.Data.WhiteTexture;
			}
			else
			{
				if(General.Map.Data.GetFlatExists(longimagename))
				{
					img = General.Map.Data.GetFlatImageKnown(longimagename);
					
					// Is the texture loaded?
					if(img.IsImageLoaded && !img.LoadFailed)
					{
						// Doom 64 animated textures (ANIMDEFS) show the current frame instead
						TextureAnimations anims = General.Map.Data.Animations;
						if((anims != null) && anims.IsAnimated(img))
						{
							animatedtextures = true;
							img = anims.Translate(img);
						}
						
						if(img.Texture == null) img.CreateTexture();
					}
					else
					{
						img = General.Map.Data.WhiteTexture;
					}
				}
				else
				{
					img = General.Map.Data.UnknownTexture3D;
				}
			}
			
			return img;
		}
		
		// This renders the sorted sector surfaces
		internal void RenderSectorSurfaces(D3DDevice graphics)
		{
			if(!resourcesunloaded)
			{
				graphics.Shaders.Display2D.Begin();
				foreach(KeyValuePair<ImageData, List<SurfaceEntry>> imgsurfaces in surfaces)
				{
					// Set texture
					graphics.Shaders.Display2D.Texture1 = imgsurfaces.Key.Texture;
					if(!graphics.Shaders.Enabled) graphics.Device.SetTexture(0, imgsurfaces.Key.Texture);

					graphics.Shaders.Display2D.BeginPass(1);
					
					// Go for all surfaces
					VertexBuffer lastbuffer = null;
					foreach(SurfaceEntry entry in imgsurfaces.Value)
					{
						// Set the vertex buffer
						SurfaceBufferSet set = sets[entry.numvertices];
						if(set.buffers[entry.bufferindex] != lastbuffer)
						{
							lastbuffer = set.buffers[entry.bufferindex];
							graphics.Device.SetStreamSource(0, lastbuffer, 0, FlatVertex.Stride);
						}

						// Draw
						graphics.Device.DrawPrimitives(PrimitiveType.TriangleList, entry.vertexoffset + (entry.numvertices * surfacevertexoffsetmul), entry.numvertices / 3);
					}
					
					graphics.Shaders.Display2D.EndPass();
				}
				graphics.Shaders.Display2D.End();
				
				// Flats that move in the game
				RenderScrollingSurfaces(graphics);
			}
		}
		
		// This draws the sectors with moving flats (Doom 64 scrolling sectors and liquid floors) and light effects, using
		// the same offsets and glow as the 3D mode. A liquid floor is drawn the way the game does: the flat after the
		// floor flat as the opaque bottom layer and the floor flat over it as a translucent layer.
		private void RenderScrollingSurfaces(D3DDevice graphics)
		{
			if(scrollentries.Count == 0) return;
			
			foreach(SurfaceEntry entry in scrollentries)
			{
				if((entry.numvertices <= 0) || (entry.bufferindex < 0) || !sets.ContainsKey(entry.numvertices)) continue;
				
				Sector s = entry.sector;
				SurfaceBufferSet set = sets[entry.numvertices];
				VertexBuffer vb = set.buffers[entry.bufferindex];
				int firstvertex = entry.vertexoffset + (entry.numvertices * surfacevertexoffsetmul);
				float du, dv;
				
				// Light effect of the sector. In the color view modes the texture is plain white, so the light
				// is added to the color that is shown. With flats it is added to the flat, like the game does.
				bool light = SectorGlow.IsAnimated(s);
				float glow = light ? SectorGlow.GetGlow(s) : 0.0f;
				
				if(!scrolltextured)
				{
					// Plain sector colors
					DrawScrollingEntry(graphics, vb, firstvertex, entry.numvertices, GetImageForRendering(0), 0.0f, 0.0f, 1.0f, false, light, glow, false);
				}
				else if(!scrollceiling && TextureScroll.IsLiquid(s))
				{
					// Opaque bottom layer
					bool havebase = false;
					string basename = TextureScroll.GetLiquidBaseName(s);
					if((basename != null) && TextureScroll.GetSectorLiquidOffset(s, false, out du, out dv))
					{
						ImageData baseimg = GetImageForRendering(CodeImp.DoomBuilder.IO.Lump.MakeLongName(basename));
						DrawScrollingEntry(graphics, vb, firstvertex, entry.numvertices, baseimg, du, dv, 1.0f, false, light, glow, true);
						havebase = true;
					}
					
					// Translucent top layer (alpha 160 of 255 in the game)
					if(TextureScroll.GetSectorLiquidOffset(s, true, out du, out dv))
					{
						ImageData topimg = GetImageForRendering(entry.floortexture);
						DrawScrollingEntry(graphics, vb, firstvertex, entry.numvertices, topimg, du, dv, havebase ? (160.0f / 255.0f) : 1.0f, havebase, light, glow, true);
					}
				}
				else
				{
					long longname = scrollceiling ? entry.ceiltexture : entry.floortexture;
					if(!TextureScroll.GetSectorPlaneOffset(s, scrollceiling, out du, out dv)) { du = 0.0f; dv = 0.0f; }
					DrawScrollingEntry(graphics, vb, firstvertex, entry.numvertices, GetImageForRendering(longname), du, dv, 1.0f, false, light, glow, true);
				}
			}
			
			// Restore the settings for everything that is drawn after this
			graphics.Shaders.Display2D.SetUVOffset(0.0f, 0.0f);
			graphics.Shaders.Display2D.SetGlow(0.0f, false);
			graphics.Shaders.Display2D.SetSettings(1f, 1f, 0f, 1f, General.Settings.ClassicBilinear);
			graphics.Device.SetRenderState(RenderState.AlphaBlendEnable, false);
			graphics.Device.SetRenderState(RenderState.ColorWriteEnable, ColorWriteEnable.Red | ColorWriteEnable.Green | ColorWriteEnable.Blue | ColorWriteEnable.Alpha);
		}
		
		// This draws one sector surface with a texture coordinate offset and sector light effect
		private void DrawScrollingEntry(D3DDevice graphics, VertexBuffer vb, int firstvertex, int numvertices, ImageData img, float du, float dv, float alpha, bool blend, bool light, float glow, bool glowbeforecolor)
		{
			Display2DShader shader = graphics.Shaders.Display2D;
			
			shader.SetUVOffset(du, dv);
			shader.SetGlow(glow, glowbeforecolor);
			shader.SetSettings(1f, 1f, 0f, alpha, General.Settings.ClassicBilinear);
			shader.Texture1 = img.Texture;
			if(!graphics.Shaders.Enabled) graphics.Device.SetTexture(0, img.Texture);
			
			// A translucent layer must not write the alpha channel, because the surface is
			// drawn into a texture that is blended over the background afterwards
			graphics.Device.SetRenderState(RenderState.AlphaBlendEnable, blend);
			graphics.Device.SetRenderState(RenderState.ColorWriteEnable, blend ?
				(ColorWriteEnable.Red | ColorWriteEnable.Green | ColorWriteEnable.Blue) :
				(ColorWriteEnable.Red | ColorWriteEnable.Green | ColorWriteEnable.Blue | ColorWriteEnable.Alpha));
			
			graphics.Device.SetStreamSource(0, vb, 0, FlatVertex.Stride);
			shader.Begin();
			shader.BeginPass(light ? 4 : 3);
			shader.ApplySettings();
			graphics.Device.DrawPrimitives(PrimitiveType.TriangleList, firstvertex, numvertices / 3);
			shader.EndPass();
			shader.End();
		}
		
		#endregion
	}
}
