﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace LottieUWP
{
    /// <summary>
    /// This can be used to show an lottie animation in any place that would normally take a drawable.
    /// If there are masks or mattes, then you MUST call <seealso cref="RecycleBitmaps()"/> when you are done
    /// or else you will leak bitmaps.
    /// <para>
    /// It is preferable to use <seealso cref="LottieAnimationView"/> when possible because it
    /// handles bitmap recycling and asynchronous loading
    /// of compositions.
    /// </para>
    /// </summary>
    public class LottieDrawable : UserControl
    {
        private Matrix3X3 _matrix = Matrix3X3.CreateIdentity();
        private LottieComposition _composition;
        private readonly ValueAnimator _animator = ValueAnimator.OfFloat(0f, 1f);
        private float _speed = 1f;
        private float _scale = 1f;
        private float _progress;

        private readonly ISet<ColorFilterData> _colorFilterData = new HashSet<ColorFilterData>();
        private ImageAssetManager _imageAssetManager;
        private IImageAssetDelegate _imageAssetDelegate;
        private FontAssetManager _fontAssetManager;
        private FontAssetDelegate _fontAssetDelegate;
        private TextDelegate _textDelegate;
        private bool _playAnimationWhenCompositionAdded;
        private bool _reverseAnimationWhenCompositionAdded;
        private bool _systemAnimationsAreDisabled;
        private bool _enableMergePaths;
        private CompositionLayer _compositionLayer;
        private byte _alpha = 255;
        private bool _performanceTrackingEnabled;
        private BitmapCanvas _bitmapCanvas;
        private CanvasAnimatedControl _canvasControl;
        private bool _forceSoftwareRenderer;

        public LottieDrawable()
        {
            _animator.Loop = false;
            _animator.Interpolator = new LinearInterpolator();
            _animator.Update += (sender, e) =>
            {
                if (_systemAnimationsAreDisabled)
                {
                    _animator.Cancel();
                    Progress = 1f;
                }
                else
                {
                    Progress = e.Animation.AnimatedValue;
                }
            };
            Loaded += UserControl_Loaded;
            Unloaded += UserControl_Unloaded;
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            _canvasControl = new CanvasAnimatedControl
            {
                ForceSoftwareRenderer = _forceSoftwareRenderer
            };

            _canvasControl.Draw += CanvasControlOnDraw;
            Content = _canvasControl;
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            // Explicitly remove references to allow the Win2D controls to get garbage collected
            if (_canvasControl != null)
            {
                _canvasControl.RemoveFromVisualTree();
                _canvasControl = null;
            }
        }

        public void ForceSoftwareRenderer(bool force)
        {
            _forceSoftwareRenderer = force;
            if (_canvasControl != null)
            {
                _canvasControl.ForceSoftwareRenderer = force;
            }
        }

        /// <summary>
        /// Returns whether or not any layers in this composition has masks.
        /// </summary>
        public virtual bool HasMasks()
        {
            return _compositionLayer != null && _compositionLayer.HasMasks();
        }

        /// <summary>
        /// Returns whether or not any layers in this composition has a matte layer.
        /// </summary>
        public virtual bool HasMatte()
        {
            return _compositionLayer != null && _compositionLayer.HasMatte();
        }

        internal virtual bool EnableMergePathsForKitKatAndAbove()
        {
            return _enableMergePaths;
        }

        /// <summary>
        /// Enable this to get merge path support for devices running KitKat (19) and above.
        /// 
        /// Merge paths currently don't work if the the operand shape is entirely contained within the
        /// first shape. If you need to cut out one shape from another shape, use an even-odd fill type
        /// instead of using merge paths.
        /// </summary>
        public virtual void EnableMergePathsForKitKatAndAbove(bool enable)
        {
            _enableMergePaths = enable;
            if (_composition != null)
            {
                BuildCompositionLayer();
            }
        }

        /// <summary>
        /// If you use image assets, you must explicitly specify the folder in assets/ in which they are
        /// located because bodymovin uses the name filenames across all compositions (img_#).
        /// Do NOT rename the images themselves.
        /// 
        /// If your images are located in src/main/assets/airbnb_loader/ then call
        /// `setImageAssetsFolder("airbnb_loader/");`.
        /// 
        /// 
        /// If you use LottieDrawable directly, you MUST call <seealso cref="RecycleBitmaps()"/> when you
        /// are done. Calling <seealso cref="RecycleBitmaps()"/> doesn't have to be final and <seealso cref="LottieDrawable"/>
        /// will recreate the bitmaps if needed but they will leak if you don't recycle them.
        /// </summary>
        public virtual string ImageAssetsFolder { get; set; }

        /// <summary>
        /// If you have image assets and use <seealso cref="LottieDrawable"/> directly, you must call this yourself.
        /// 
        /// Calling recycleBitmaps() doesn't have to be final and <seealso cref="LottieDrawable"/>
        /// will recreate the bitmaps if needed but they will leak if you don't recycle them.
        /// 
        /// </summary>
        public virtual void RecycleBitmaps()
        {
            _imageAssetManager?.RecycleBitmaps();
        }

        /// <returns> True if the composition is different from the previously set composition, false otherwise. </returns>
        public virtual bool SetComposition(LottieComposition composition)
        {
            //if (Callback == null) // TODO: needed?
            //{
            //    throw new System.InvalidOperationException("You or your view must set a Drawable.Callback before setting the composition. This " + "gets done automatically when added to an ImageView. " + "Either call ImageView.setImageDrawable() before setComposition() or call " + "setCallback(yourView.getCallback()) first.");
            //}

            if (_composition == composition)
            {
                return false;
            }

            lock (this)
            {
                ClearComposition();
                _composition = composition;
                Speed = _speed;
                Scale = 1f;
                UpdateBounds();
                BuildCompositionLayer();
                ApplyColorFilters();

                Progress = _progress;
                if (_playAnimationWhenCompositionAdded)
                {
                    _playAnimationWhenCompositionAdded = false;
                    PlayAnimation();
                }
                if (_reverseAnimationWhenCompositionAdded)
                {
                    _reverseAnimationWhenCompositionAdded = false;
                    ReverseAnimation();
                }
                composition.PerformanceTrackingEnabled = _performanceTrackingEnabled;
            }

            return true;
        }

        public virtual bool PerformanceTrackingEnabled
        {
            set
            {
                _performanceTrackingEnabled = value;
                if (_composition != null)
                {
                    _composition.PerformanceTrackingEnabled = value;
                }
            }
        }

        public virtual PerformanceTracker PerformanceTracker => _composition?.PerformanceTracker;

        private void BuildCompositionLayer()
        {
            _compositionLayer = new CompositionLayer(this, Layer.Factory.NewInstance(_composition), _composition.Layers, _composition);

            _bitmapCanvas = new BitmapCanvas(Width, Height);
        }

        private void ApplyColorFilters()
        {
            if (_compositionLayer == null)
            {
                return;
            }

            foreach (var data in _colorFilterData)
            {
                _compositionLayer.AddColorFilter(data.LayerName, data.ContentName, data.ColorFilter);
            }
        }

        private void ClearComposition()
        {
            RecycleBitmaps();
            _compositionLayer = null;
            _imageAssetManager = null;
            InvalidateSelf();
        }

        public void InvalidateSelf()
        {
            _canvasControl?.Invalidate();
        }

        public void SetAlpha(byte alpha)
        {
            _alpha = alpha;
        }

        public int GetAlpha()
        {
            return _alpha;
        }

        public ColorFilter ColorFilter
        {
            set
            {
                // Do nothing.
            }
        }

        /// <summary>
        /// Add a color filter to specific content on a specific layer. </summary>
        /// <param name="layerName"> name of the layer where the supplied content name lives </param>
        /// <param name="contentName"> name of the specific content that the color filter is to be applied </param>
        /// <param name="colorFilter"> the color filter, null to clear the color filter </param>
        public virtual void AddColorFilterToContent(string layerName, string contentName, ColorFilter colorFilter)
        {
            AddColorFilterInternal(layerName, contentName, colorFilter);
        }

        /// <summary>
        /// Add a color filter to a whole layer </summary>
        /// <param name="layerName"> name of the layer that the color filter is to be applied </param>
        /// <param name="colorFilter"> the color filter, null to clear the color filter </param>
        public virtual void AddColorFilterToLayer(string layerName, ColorFilter colorFilter)
        {
            AddColorFilterInternal(layerName, null, colorFilter);
        }

        /// <summary>
        /// Add a color filter to all layers </summary>
        /// <param name="colorFilter"> the color filter, null to clear all color filters </param>
        public virtual void AddColorFilter(ColorFilter colorFilter)
        {
            AddColorFilterInternal(null, null, colorFilter);
        }

        /// <summary>
        /// Clear all color filters on all layers and all content in the layers
        /// </summary>
        public virtual void ClearColorFilters()
        {
            _colorFilterData.Clear();
            AddColorFilterInternal(null, null, null);
        }

        /// <summary>
        /// Private method to capture all color filter additions.
        /// There are 3 different behaviors here.
        /// 1. layerName is null. All layers supporting color filters will apply the passed in color filter
        /// 2. layerName is not null, contentName is null. This will apply the passed in color filter
        ///    to the whole layer
        /// 3. layerName is not null, contentName is not null. This will apply the pass in color filter
        ///    to a specific composition content.
        /// </summary>
        private void AddColorFilterInternal(string layerName, string contentName, ColorFilter colorFilter)
        {
            var data = new ColorFilterData(layerName, contentName, colorFilter);
            if (colorFilter == null && _colorFilterData.Contains(data))
            {
                _colorFilterData.Remove(data);
            }
            else
            {
                _colorFilterData.Add(new ColorFilterData(layerName, contentName, colorFilter));
            }

            _compositionLayer?.AddColorFilter(layerName, contentName, colorFilter);
        }

        //public int Opacity
        //{
        //    get
        //    {
        //        return PixelFormat.TRANSLUCENT;
        //    }
        //}

        private void CanvasControlOnDraw(ICanvasAnimatedControl canvasControl, CanvasAnimatedDrawEventArgs args)
        {
            lock (this)
            {
                using (_bitmapCanvas.CreateSession(canvasControl.Device, args.DrawingSession))
                {
                    _bitmapCanvas.Clear(Colors.Transparent);
                    LottieLog.BeginSection("Drawable.Draw");
                    if (_compositionLayer == null)
                    {
                        return;
                    }
                    var scale = _scale;
                    if (_compositionLayer.HasMatte())
                    {
                        scale = Math.Min(_scale, GetMaxScale(_bitmapCanvas));
                    }

                    _matrix.Reset();
                    _matrix = MatrixExt.PreScale(_matrix, scale, scale);
                    _compositionLayer.Draw(_bitmapCanvas, _matrix, _alpha);
                    LottieLog.EndSection("Drawable.Draw");
                }
            }
        }

        internal virtual void SystemAnimationsAreDisabled()
        {
            _systemAnimationsAreDisabled = true;
        }

        public virtual bool Looping
        {
            get => _animator.Loop;
            set => _animator.Loop = value;
        }

        public virtual bool IsAnimating => _animator.IsRunning;

        public virtual void PlayAnimation()
        {
            PlayAnimation(_progress > 0.0 && _progress < 1.0);
        }

        public virtual void ResumeAnimation()
        {
            PlayAnimation(true);
        }

        private void PlayAnimation(bool setStartTime)
        {
            if (_compositionLayer == null)
            {
                _playAnimationWhenCompositionAdded = true;
                _reverseAnimationWhenCompositionAdded = false;
                return;
            }
            var playTime = setStartTime ? (long)(_progress * _animator.Duration) : 0;
            _animator.Start();
            if (setStartTime)
            {
                _animator.CurrentPlayTime = playTime;
            }
        }

        public virtual void ResumeReverseAnimation()
        {
            ReverseAnimation(true);
        }

        public virtual void ReverseAnimation()
        {
            ReverseAnimation(_progress > 0.0 && _progress < 1.0);
        }

        private void ReverseAnimation(bool setStartTime)
        {
            if (_compositionLayer == null)
            {
                _playAnimationWhenCompositionAdded = false;
                _reverseAnimationWhenCompositionAdded = true;
                return;
            }
            if (setStartTime)
            {
                _animator.CurrentPlayTime = (long)(_progress * _animator.Duration);
            }
            _animator.Reverse();
        }

        public virtual float Speed
        {
            set
            {
                _speed = value;
                if (value < 0)
                {
                    _animator.SetFloatValues(1f, 0f);
                }
                else
                {
                    _animator.SetFloatValues(0f, 1f);
                }

                if (_composition != null)
                {
                    _animator.Duration = (long)(_composition.Duration / Math.Abs(value));
                }
            }
        }

        public virtual float Progress
        {
            get => _progress;
            set
            {
                _progress = value;
                _animator.Progress = value;
                if (_compositionLayer != null)
                {
                    _compositionLayer.Progress = value;
                }
            }
        }

        /// <summary>
        /// Use this to manually set fonts. 
        /// </summary>
        public virtual FontAssetDelegate FontAssetDelegate
        {
            set
            {
                _fontAssetDelegate = value;
                if (_fontAssetManager != null)
                {
                    _fontAssetManager.Delegate = value;
                }
            }
        }

        public virtual TextDelegate TextDelegate
        {
            set => _textDelegate = value;
            get => _textDelegate;
        }

        internal virtual bool UseTextGlyphs()
        {
            return _textDelegate == null && _composition.Characters.Count > 0;
        }

        /// <summary>
        /// Set the scale on the current composition. The only cost of this function is re-rendering the
        /// current frame so you may call it frequent to scale something up or down.
        /// 
        /// The smaller the animation is, the better the performance will be. You may find that scaling an
        /// animation down then rendering it in a larger ImageView and letting ImageView scale it back up
        /// with a scaleType such as centerInside will yield better performance with little perceivable
        /// quality loss.
        /// </summary>
        public virtual float Scale
        {
            set
            {
                _scale = value;
                UpdateBounds();
            }
            get => _scale;
        }

        /// <summary>
        /// Use this if you can't bundle images with your app. This may be useful if you download the
        /// animations from the network or have the images saved to an SD Card. In that case, Lottie
        /// will defer the loading of the bitmap to this delegate.
        /// </summary>
        public virtual IImageAssetDelegate ImageAssetDelegate
        {
            set
            {
                _imageAssetDelegate = value;
                if (_imageAssetManager != null)
                {
                    _imageAssetManager.Delegate = value;
                }
            }
        }

        public virtual LottieComposition Composition => _composition;

        private void UpdateBounds()
        {
            if (_composition == null)
            {
                return;
            }
            Width = (int)(_composition.Bounds.Width * _scale);
            Height = (int)(_composition.Bounds.Height * _scale);
        }

        public virtual void CancelAnimation()
        {
            _playAnimationWhenCompositionAdded = false;
            _reverseAnimationWhenCompositionAdded = false;
            _animator.Cancel();
        }

        public event EventHandler<ValueAnimator.ValueAnimatorUpdateEventArgs> AnimatorUpdate
        {
            add => _animator.Update += value;
            remove => _animator.Update -= value;
        }

        public event EventHandler ValueChanged
        {
            add => _animator.ValueChanged += value;
            remove => _animator.ValueChanged -= value;
        }

        public int IntrinsicWidth => _composition == null ? -1 : (int)(_composition.Bounds.Width * _scale);

        public int IntrinsicHeight => _composition == null ? -1 : (int)(_composition.Bounds.Height * _scale);

        /// 
        /// <summary>
        /// Allows you to modify or clear a bitmap that was loaded for an image either automatically
        /// 
        /// through <seealso cref="ImageAssetsFolder"/> or with an <seealso cref="ImageAssetDelegate"/>.
        /// 
        /// 
        /// </summary>
        /// <returns> the previous Bitmap or null.
        ///  </returns>
        public virtual CanvasBitmap UpdateBitmap(string id, CanvasBitmap bitmap)
        {
            var bm = ImageAssetManager;
            if (bm == null)
            {
                Debug.WriteLine("Cannot update bitmap. Most likely the drawable is not added to a View " + "which prevents Lottie from getting a Context.", LottieLog.Tag);
                return null;
            }
            var ret = bm.UpdateBitmap(id, bitmap);
            InvalidateSelf();
            return ret;
        }

        internal virtual CanvasBitmap GetImageAsset(string id)
        {
            return ImageAssetManager?.BitmapForId(_canvasControl.Device, id);
        }

        private ImageAssetManager ImageAssetManager
        {
            get
            {
                if (_imageAssetManager != null && false)//!_imageAssetManager.hasSameContext(Context))
                {
                    _imageAssetManager.RecycleBitmaps();
                    _imageAssetManager = null;
                }

                if (_imageAssetManager == null)
                {
                    _imageAssetManager = new ImageAssetManager(ImageAssetsFolder, _imageAssetDelegate, _composition.Images);
                }

                return _imageAssetManager;
            }
        }

        internal virtual Typeface GetTypeface(string fontFamily, string style)
        {
            var assetManager = FontAssetManager;
            return assetManager?.GetTypeface(fontFamily, style);
        }

        private FontAssetManager FontAssetManager => _fontAssetManager ??
            (_fontAssetManager = new FontAssetManager(_fontAssetDelegate));

        private float GetMaxScale(BitmapCanvas canvas)
        {
            var maxScaleX = (float)canvas.Width / (float)_composition.Bounds.Width;
            var maxScaleY = (float)canvas.Height / (float)_composition.Bounds.Height;
            return Math.Min(maxScaleX, maxScaleY);
        }

        private class ColorFilterData
        {
            internal readonly string LayerName;
            internal readonly string ContentName;
            internal readonly ColorFilter ColorFilter;

            internal ColorFilterData(string layerName, string contentName, ColorFilter colorFilter)
            {
                LayerName = layerName;
                ContentName = contentName;
                ColorFilter = colorFilter;
            }

            public override int GetHashCode()
            {
                var hashCode = 17;
                if (!string.IsNullOrEmpty(LayerName))
                {
                    hashCode = hashCode * 31 * LayerName.GetHashCode();
                }

                if (!string.IsNullOrEmpty(ContentName))
                {
                    hashCode = hashCode * 31 * ContentName.GetHashCode();
                }
                return hashCode;
            }

            public override bool Equals(object obj)
            {
                if (this == obj)
                {
                    return true;
                }

                if (!(obj is ColorFilterData))
                {
                    return false;
                }

                var other = (ColorFilterData)obj;

                return GetHashCode() == other.GetHashCode() && ColorFilter == other.ColorFilter;
            }
        }
    }
}