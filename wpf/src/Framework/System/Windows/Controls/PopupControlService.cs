//---------------------------------------------------------------------------
//
// Copyright (C) Microsoft Corporation.  All rights reserved.
//
//---------------------------------------------------------------------------

using MS.Internal;
using MS.Internal.KnownBoxes;
using MS.Internal.Media;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Security;
using System.Security.Permissions;

namespace System.Windows.Controls
{
    /// <summary>
    ///     Service class that provides the input for the ContextMenu and ToolTip services.
    /// </summary>
    internal sealed class PopupControlService
    {
        #region Creation

        ///<SecurityNote>
        ///     Critical - this function elevates by accessing InputManger.Current.
        ///     TreatAsSafe: This code simply attaches a call back which is private
        ///</SecurityNote>
        [SecurityCritical,SecurityTreatAsSafe]
        internal PopupControlService()
        {
            InputManager.Current.PostProcessInput += new ProcessInputEventHandler(OnPostProcessInput);
        }

        #endregion

        #region Input Handling

        /////////////////////////////////////////////////////////////////////
        ///<SecurityNote>
        /// Critical: accesses e.StagingItem.Input. Also this code revieves an event that has
        ///           user initiated set. Also calls PresentationSource.CriticalFromVisual.
        ///</SecurityNote>
        [SecurityCritical]
        private void OnPostProcessInput(object sender, ProcessInputEventArgs e)
        {
            if (e.StagingItem.Input.RoutedEvent == InputManager.InputReportEvent)
            {
                InputReportEventArgs report = (InputReportEventArgs)e.StagingItem.Input;
                if (!report.Handled)
                {
                    if (report.Report.Type == InputType.Mouse)
                    {
                        RawMouseInputReport mouseReport = (RawMouseInputReport)report.Report;
                        if ((mouseReport.Actions & RawMouseActions.AbsoluteMove) == RawMouseActions.AbsoluteMove)
                        {
                            if ((Mouse.LeftButton == MouseButtonState.Pressed) ||
                                (Mouse.RightButton == MouseButtonState.Pressed))
                            {
                                RaiseToolTipClosingEvent(true /* reset */);
                            }
                            else
                            {
                                IInputElement directlyOver = Mouse.PrimaryDevice.RawDirectlyOver;
                                if (directlyOver != null)
                                {
                                    Point pt = Mouse.PrimaryDevice.GetPosition(directlyOver);

                                    // If possible, check that the mouse position is within the render bounds
                                    // (avoids mouse capture confusion).
                                    if (Mouse.CapturedMode != CaptureMode.None)
                                    {
                                        // Get the root visual
                                        PresentationSource source = PresentationSource.CriticalFromVisual((DependencyObject)directlyOver);
                                        UIElement rootAsUIElement = source != null ? source.RootVisual as UIElement : null;
                                        if (rootAsUIElement != null)
                                        {
                                            // Get mouse position wrt to root
                                            pt = Mouse.PrimaryDevice.GetPosition(rootAsUIElement);

                                            // Hittest to find the element the mouse is over
                                            IInputElement enabledHit;
                                            rootAsUIElement.InputHitTest(pt, out enabledHit, out directlyOver);

                                            // Find the position of the mouse relative the element that the mouse is over
                                            pt = Mouse.PrimaryDevice.GetPosition(directlyOver);
                                        }
                                        else
                                        {
                                            directlyOver = null;
                                        }
                                    }

                                    if (directlyOver != null)
                                    {
                                        // Process the mouse move
                                        OnMouseMove(directlyOver, pt);
                                    }
                                }
                            }
                        }
                        else if ((mouseReport.Actions & RawMouseActions.Deactivate) == RawMouseActions.Deactivate)
                        {
                            if (LastMouseDirectlyOver != null)
                            {
                                LastMouseDirectlyOver = null;
                                if (LastMouseOverWithToolTip != null)
                                {
                                    RaiseToolTipClosingEvent(true /* reset */);

                                    // When the user moves the cursor outside of the window,
                                    // clear the LastMouseOverWithToolTip property so if the user returns
                                    // the mouse to the same item, the tooltip will reappear.  If
                                    // the deactivation is coming from a window grabbing capture
                                    // (such as Drag and Drop) do not clear the property.
                                    if (MS.Win32.SafeNativeMethods.GetCapture() == IntPtr.Zero)
                                    {
                                        LastMouseOverWithToolTip = null;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            else if (e.StagingItem.Input.RoutedEvent == Keyboard.KeyDownEvent)
            {
                ProcessKeyDown(sender, (KeyEventArgs)e.StagingItem.Input);
            }
            else if (e.StagingItem.Input.RoutedEvent == Keyboard.KeyUpEvent)
            {
                ProcessKeyUp(sender, (KeyEventArgs)e.StagingItem.Input);
            }
            else if (e.StagingItem.Input.RoutedEvent == Mouse.MouseUpEvent)
            {
                ProcessMouseUp(sender, (MouseButtonEventArgs)e.StagingItem.Input);
            }
            else if (e.StagingItem.Input.RoutedEvent == Mouse.MouseDownEvent)
            {
                RaiseToolTipClosingEvent(true /* reset */);
            }
        }

        private void OnMouseMove(IInputElement directlyOver, Point pt)
        {
            if (directlyOver != LastMouseDirectlyOver)
            {
                LastMouseDirectlyOver = directlyOver;
                if (directlyOver != LastMouseOverWithToolTip)
                {
                    InspectElementForToolTip(directlyOver as DependencyObject);
                }
            }
        }
        /////////////////////////////////////////////////////////////////////
        ///<SecurityNote>
        /// Critical: This has the ability to spoof input for paste
        ///</SecurityNote>
        [SecurityCritical]
        private void ProcessMouseUp(object sender, MouseButtonEventArgs e)
        {
            RaiseToolTipClosingEvent(false /* reset */);

            if (!e.Handled)
            {
                if ((e.ChangedButton == MouseButton.Right) &&
                    (e.RightButton == MouseButtonState.Released))
                {
                    IInputElement directlyOver = Mouse.PrimaryDevice.RawDirectlyOver;
                    if (directlyOver != null)
                    {
                        Point pt = Mouse.PrimaryDevice.GetPosition(directlyOver);
                        if (RaiseContextMenuOpeningEvent(directlyOver, pt.X, pt.Y,e.UserInitiated))
                        {
                            e.Handled = true;
                        }
                    }
                }
            }
        }

        /////////////////////////////////////////////////////////////////////
        ///<SecurityNote>
        /// Critical: This has the ability to spoof input for paste
        ///</SecurityNote>
        [SecurityCritical]
        private void ProcessKeyDown(object sender, KeyEventArgs e)
        {
            if (!e.Handled)
            {
                if ((e.SystemKey == Key.F10) && ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift))
                {
                    RaiseContextMenuOpeningEvent(e);
                }
            }
        }

        /////////////////////////////////////////////////////////////////////
        ///<SecurityNote>
        /// Critical: This has the ability to spoof input for paste
        ///</SecurityNote>
        [SecurityCritical]
        private void ProcessKeyUp(object sender, KeyEventArgs e)
        {
            if (!e.Handled)
            {
                if (e.Key == Key.Apps)
                {
                    RaiseContextMenuOpeningEvent(e);
                }
            }
        }

        #endregion

        #region ToolTip

        private void InspectElementForToolTip(DependencyObject o)
        {
            DependencyObject origObj = o;
            if (LocateNearestToolTip(ref o))
            {
                // Show the ToolTip on "o" or keep the current ToolTip active

                if (o != null)
                {
                    // A ToolTip value was found and is enabled, proceed to firing the event

                    if (LastMouseOverWithToolTip != null)
                    {
                        // If a ToolTip is active, don't show it anymore
                        RaiseToolTipClosingEvent(true /* reset */);
                    }

                    LastChecked = origObj;
                    LastMouseOverWithToolTip = o;

                    bool quickShow = _quickShow; // ResetToolTipTimer may reset _quickShow
                    ResetToolTipTimer();

                    if (quickShow)
                    {
                        _quickShow = false;
                        RaiseToolTipOpeningEvent();
                    }
                    else
                    {
                        ToolTipTimer = new DispatcherTimer(DispatcherPriority.Normal);
                        ToolTipTimer.Interval = TimeSpan.FromMilliseconds(ToolTipService.GetInitialShowDelay(o));
                        ToolTipTimer.Tag = BooleanBoxes.TrueBox; // should open
                        ToolTipTimer.Tick += new EventHandler(OnRaiseToolTipOpeningEvent);
                        ToolTipTimer.Start();
                    }
                }
            }
            else
            {
                // If a ToolTip is active, don't show it anymore
                RaiseToolTipClosingEvent(true /* reset */);

                // No longer over an item with a tooltip
                LastMouseOverWithToolTip = null;
            }
        }

        /// <summary>
        ///     Finds the nearest element with an enabled tooltip.
        /// </summary>
        /// <param name="o">
        ///     The most "leaf" element to start looking at.
        ///     This element will be replaced with the element that
        ///     contains an active tooltip OR null if the element
        ///     is already in play.
        /// </param>
        /// <returns>True if there is an active tooltip in play.</returns>
        private bool LocateNearestToolTip(ref DependencyObject o)
        {
            IInputElement element = o as IInputElement;
            if (element != null)
            {
                FindToolTipEventArgs args = new FindToolTipEventArgs();
                element.RaiseEvent(args);

                if (args.TargetElement != null)
                {
                    // Open this element's ToolTip
                    o = args.TargetElement;
                    return true;
                }
                else if (args.KeepCurrentActive)
                {
                    // Keep the current ToolTip active
                    o = null;
                    return true;
                }
            }

            // Close any existing ToolTips
            return false;
        }

        internal bool StopLookingForToolTip(DependencyObject o)
        {
            if ((o == LastChecked) || (o == LastMouseOverWithToolTip) || (o == _currentToolTip) || WithinCurrentToolTip(o))
            {
                // In this case, don't show the ToolTip, but the current ToolTip is still OK to show.
                return true;
            }

            return false;
        }

        private bool WithinCurrentToolTip(DependencyObject o)
        {
            // If no current tooltip, then no need to look
            if (_currentToolTip == null)
            {
                return false;
            }

            DependencyObject v = o as Visual;
            if (v == null)
            {
                ContentElement ce = o as ContentElement;
                if (ce != null)
                {
                    v = FindContentElementParent(ce);
                }
                else
                {
                    v = o as Visual3D;
                }
            }

            return (v != null) &&
                   ((v is Visual && ((Visual)v).IsDescendantOf(_currentToolTip)) ||
                    (v is Visual3D && ((Visual3D)v).IsDescendantOf(_currentToolTip)));
        }

        private void ResetToolTipTimer()
        {
            if (_toolTipTimer != null)
            {
                _toolTipTimer.Stop();
                _toolTipTimer = null;
                _quickShow = false;
            }
        }

        internal void OnRaiseToolTipOpeningEvent(object sender, EventArgs e)
        {
            RaiseToolTipOpeningEvent();
        }

        private void RaiseToolTipOpeningEvent()
        {
            ResetToolTipTimer();

            if (_forceCloseTimer != null)
            {
                OnForceClose(null, EventArgs.Empty);
            }

            DependencyObject o = LastMouseOverWithToolTip;
            if (o != null)
            {
                bool show = true;

                IInputElement element = o as IInputElement;
                if (element != null)
                {
                    ToolTipEventArgs args = new ToolTipEventArgs(true);
                    element.RaiseEvent(args);

                    show = !args.Handled;
                }

                if (show)
                {
                    object tooltip = ToolTipService.GetToolTip(o);
                    ToolTip tip = tooltip as ToolTip;
                    if (tip != null)
                    {
                        _currentToolTip = tip;
                        _ownToolTip = false;
                    }
                    else if ((_currentToolTip == null) || !_ownToolTip)
                    {
                        _currentToolTip = new ToolTip();
                        _ownToolTip = true;
                        _currentToolTip.SetValue(ServiceOwnedProperty, BooleanBoxes.TrueBox);

                        // Bind the content of the tooltip to the ToolTip attached property
                        Binding binding = new Binding();
                        binding.Path = new PropertyPath(ToolTipService.ToolTipProperty);
                        binding.Mode = BindingMode.OneWay;
                        binding.Source = o;
                        _currentToolTip.SetBinding(ToolTip.ContentProperty, binding);
                    }

                    if (!_currentToolTip.StaysOpen)
                    {
                        // The popup takes capture in this case, which causes us to hit test to the wrong window.
                        // We do not support this scenario. Cleanup and then throw and exception.
                        throw new NotSupportedException(SR.Get("SRID.ToolTipStaysOpenFalseNotAllowed"));
                    }

                    _currentToolTip.SetValue(OwnerProperty, o);
                    _currentToolTip.Closed += OnToolTipClosed;
                    _currentToolTip.IsOpen = true;

                    ToolTipTimer = new DispatcherTimer(DispatcherPriority.Normal);
                    ToolTipTimer.Interval = TimeSpan.FromMilliseconds(ToolTipService.GetShowDuration(o));
                    ToolTipTimer.Tick += new EventHandler(OnRaiseToolTipClosingEvent);
                    ToolTipTimer.Start();
                }
            }
        }

        internal void OnRaiseToolTipClosingEvent(object sender, EventArgs e)
        {
            RaiseToolTipClosingEvent(false /* reset */);
        }

        /// <summary>
        ///     Closes the current tooltip, firing a Closing event if necessary.
        /// </summary>
        /// <param name="reset">
        ///     When false, will continue to treat input as if the tooltip were open so that
        ///     the tooltip of the current element won't re-open. Example: Clicking on a button
        ///     will hide the tooltip, but when the mouse is released, the tooltip should not
        ///     appear unless the mouse is moved off and then back on the button.
        /// </param>
        private void RaiseToolTipClosingEvent(bool reset)
        {
            ResetToolTipTimer();

            if (reset)
            {
                LastChecked = null;
            }

            DependencyObject o = LastMouseOverWithToolTip;
            if (o != null)
            {
                if (_currentToolTip != null)
                {
                    bool isOpen = _currentToolTip.IsOpen;

                    try
                    {
                        if (isOpen)
                        {
                            IInputElement element = o as IInputElement;
                            if (element != null)
                            {
                                element.RaiseEvent(new ToolTipEventArgs(false));
                            }
                        }
                    }
                    finally
                    {
                        if (isOpen)
                        {
                            _currentToolTip.IsOpen = false;

                            // Setting IsOpen makes call outs to app code. So it is possible that 
                            // the _currentToolTip is ----d as a result of an action there. If that 
                            // were the case we do not need to set off the timer to close the tooltip.
                            if (_currentToolTip != null)
                            {
                                // Keep references and owner set for the fade out or slide animation
                                // Owner is released when animation completes
                                _forceCloseTimer = new DispatcherTimer(DispatcherPriority.Normal);
                                _forceCloseTimer.Interval = Popup.AnimationDelayTime;
                                _forceCloseTimer.Tick += new EventHandler(OnForceClose);
                                _forceCloseTimer.Tag = _currentToolTip;
                                _forceCloseTimer.Start();
                            }

                            _quickShow = true;
                            ToolTipTimer = new DispatcherTimer(DispatcherPriority.Normal);
                            ToolTipTimer.Interval = TimeSpan.FromMilliseconds(ToolTipService.GetBetweenShowDelay(o));
                            ToolTipTimer.Tick += new EventHandler(OnBetweenShowDelay);
                            ToolTipTimer.Start();
                        }
                        else
                        {
                            // Release owner now
                            _currentToolTip.ClearValue(OwnerProperty);

                            if (_ownToolTip)
                                BindingOperations.ClearBinding(_currentToolTip, ToolTip.ContentProperty);
                        }

                        _currentToolTip = null;
                    }
                }
            }
        }

        // Clear owner when tooltip has closed
        private void OnToolTipClosed(object sender, EventArgs e)
        {
            ToolTip toolTip = (ToolTip)sender;
            toolTip.Closed -= OnToolTipClosed;
            toolTip.ClearValue(OwnerProperty);

            if ((bool)toolTip.GetValue(ServiceOwnedProperty))
            {
                BindingOperations.ClearBinding(toolTip, ToolTip.ContentProperty);
            }
        }

        // The previous tooltip hasn't closed and we are trying to open a new one
        private void OnForceClose(object sender, EventArgs e)
        {
            _forceCloseTimer.Stop();
            ToolTip toolTip = (ToolTip)_forceCloseTimer.Tag;
            toolTip.ForceClose();
            _forceCloseTimer = null;
        }

        private void OnBetweenShowDelay(object source, EventArgs e)
        {
            ResetToolTipTimer();
        }

        private IInputElement LastMouseDirectlyOver
        {
            get
            {
                if (_lastMouseDirectlyOver != null)
                {
                    IInputElement e = (IInputElement)_lastMouseDirectlyOver.Target;
                    if (e != null)
                    {
                        return e;
                    }
                    else
                    {
                        // Stale reference
                        _lastMouseDirectlyOver = null;
                    }
                }

                return null;
            }

            set
            {
                if (value == null)
                {
                    _lastMouseDirectlyOver = null;
                }
                else if (_lastMouseDirectlyOver == null)
                {
                    _lastMouseDirectlyOver = new WeakReference(value);
                }
                else
                {
                    _lastMouseDirectlyOver.Target = value;
                }
            }
        }

        private DependencyObject LastMouseOverWithToolTip
        {
            get
            {
                if (_lastMouseOverWithToolTip != null)
                {
                    DependencyObject o = (DependencyObject)_lastMouseOverWithToolTip.Target;
                    if (o != null)
                    {
                        return o;
                    }
                    else
                    {
                        // Stale reference
                        _lastMouseOverWithToolTip = null;
                    }
                }

                return null;
            }

            set
            {
                if (value == null)
                {
                    _lastMouseOverWithToolTip = null;
                }
                else if (_lastMouseOverWithToolTip == null)
                {
                    _lastMouseOverWithToolTip = new WeakReference(value);
                }
                else
                {
                    _lastMouseOverWithToolTip.Target = value;
                }
            }
        }

        private DependencyObject LastChecked
        {
            get
            {
                if (_lastChecked != null)
                {
                    DependencyObject o = (DependencyObject)_lastChecked.Target;
                    if (o != null)
                    {
                        return o;
                    }
                    else
                    {
                        // Stale reference
                        _lastChecked = null;
                    }
                }

                return null;
            }

            set
            {
                if (value == null)
                {
                    _lastChecked = null;
                }
                else if (_lastChecked == null)
                {
                    _lastChecked = new WeakReference(value);
                }
                else
                {
                    _lastChecked.Target = value;
                }
            }
        }

        #endregion

        #region ContextMenu

        /// <summary>
        ///     Event that fires on ContextMenu when it opens.
        ///     Located here to avoid circular dependencies.
        /// </summary>
        internal static readonly RoutedEvent ContextMenuOpenedEvent =
            EventManager.RegisterRoutedEvent("Opened", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(PopupControlService));

        /// <summary>
        ///     Event that fires on ContextMenu when it closes.
        ///     Located here to avoid circular dependencies.
        /// </summary>
        internal static readonly RoutedEvent ContextMenuClosedEvent =
            EventManager.RegisterRoutedEvent("Closed", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(PopupControlService));

        /////////////////////////////////////////////////////////////////////
        ///<SecurityNote>
        /// Critical: This has the ability to spoof input for paste since it passes user initiated bit
        ///</SecurityNote>
        [SecurityCritical]
        private void RaiseContextMenuOpeningEvent(KeyEventArgs e)
        {
            IInputElement source = e.OriginalSource as IInputElement;
            if (source != null)
            {
                if (RaiseContextMenuOpeningEvent(source, -1.0, -1.0,e.UserInitiated))
                {
                    e.Handled = true;
                }
            }
        }

        ///<SecurityNote>
        /// Critical: This has the ability to spoof input for paste since it passes user initiated bit
        ///</SecurityNote>
        [SecurityCritical]
        private bool RaiseContextMenuOpeningEvent(IInputElement source, double x, double y,bool userInitiated)
        {
            // Fire the event
            ContextMenuEventArgs args = new ContextMenuEventArgs(source, true /* opening */, x, y);
            DependencyObject sourceDO = source as DependencyObject;
            if (userInitiated && sourceDO != null)
            {
                if (InputElement.IsUIElement(sourceDO))
                {
                    ((UIElement)sourceDO).RaiseEvent(args, userInitiated);
                }
                else if (InputElement.IsContentElement(sourceDO))
                {
                    ((ContentElement)sourceDO).RaiseEvent(args, userInitiated);
                }
                else if (InputElement.IsUIElement3D(sourceDO))
                {
                    ((UIElement3D)sourceDO).RaiseEvent(args, userInitiated);
                }
                else
                {
                    source.RaiseEvent(args);
                }
            }
            else
            {
                source.RaiseEvent(args);
            }


            if (!args.Handled)
            {
                // No one handled the event, auto show any available ContextMenus

                // Saved from the bubble up the tree where we looked for a set ContextMenu property
                DependencyObject o = args.TargetElement;
                if ((o != null) && ContextMenuService.ContextMenuIsEnabled(o))
                {
                    // Retrieve the value
                    object menu = ContextMenuService.GetContextMenu(o);
                    ContextMenu cm = menu as ContextMenu;
                    cm.SetValue(OwnerProperty, o);
                    cm.Closed += new RoutedEventHandler(OnContextMenuClosed);

                    if ((x == -1.0) && (y == -1.0))
                    {
                        // We infer this to mean that the ContextMenu was opened with the keyboard
                        cm.Placement = PlacementMode.Center;
                    }
                    else
                    {
                        // If there is a CursorLeft and CursorTop, it was opened with the mouse.
                        cm.Placement = PlacementMode.MousePoint;
                    }

                    // Clear any open tooltips
                    RaiseToolTipClosingEvent(true /*reset */);

                    cm.SetCurrentValueInternal(ContextMenu.IsOpenProperty, BooleanBoxes.TrueBox);

                    return true; // A menu was opened
                }

                return false; // There was no menu to open
            }

            // Clear any open tooltips since someone else opened one
            RaiseToolTipClosingEvent(true /*reset */);

            return true; // The event was handled by someone else
        }


        private void OnContextMenuClosed(object source, RoutedEventArgs e)
        {
            ContextMenu cm = source as ContextMenu;
            if (cm != null)
            {
                cm.Closed -= OnContextMenuClosed;

                DependencyObject o = (DependencyObject)cm.GetValue(OwnerProperty);
                if (o != null)
                {
                    cm.ClearValue(OwnerProperty);

                    UIElement uie = GetTarget(o);
                    if (uie != null)
                    {
                        if (!IsPresentationSourceNull(uie))
                        {
                            IInputElement inputElement = (o is ContentElement || o is UIElement3D) ? (IInputElement)o : (IInputElement)uie;
                            ContextMenuEventArgs args = new ContextMenuEventArgs(inputElement, false /*opening */);
                            inputElement.RaiseEvent(args);
                        }
                    }
                }
            }
        }

        /// <SecurityNote>
        /// Critical - as this gets PresentationSource.
        /// Safe - as the value is not returned but is only checked for null.
        /// </SecurityNote>
        [SecurityCritical] //, SecurityTreatAsSafe]
        private static bool IsPresentationSourceNull(DependencyObject uie)
        {
            return PresentationSource.CriticalFromVisual(uie) == null;
        }

        #endregion

        #region Helpers

        internal static DependencyObject FindParent(DependencyObject o)
        {
            // see if o is a Visual or a Visual3D
            DependencyObject v = o as Visual;
            if (v == null)
            {
                v = o as Visual3D;
            }

            ContentElement ce = (v == null) ? o as ContentElement : null;

            if (ce != null)
            {
                o = ContentOperations.GetParent(ce);
                if (o != null)
                {
                    return o;
                }
                else
                {
                    FrameworkContentElement fce = ce as FrameworkContentElement;
                    if (fce != null)
                    {
                        return fce.Parent;
                    }
                }
            }
            else if (v != null)
            {
                return VisualTreeHelper.GetParent(v);
            }

            return null;
        }

        internal static DependencyObject FindContentElementParent(ContentElement ce)
        {
            DependencyObject nearestVisual = null;
            DependencyObject o = ce;

            while (o != null)
            {
                nearestVisual = o as Visual;
                if (nearestVisual != null)
                {
                    break;
                }

                nearestVisual = o as Visual3D;
                if (nearestVisual != null)
                {
                    break;
                }

                ce = o as ContentElement;
                if (ce != null)
                {
                    o = ContentOperations.GetParent(ce);
                    if (o == null)
                    {
                        FrameworkContentElement fce = ce as FrameworkContentElement;
                        if (fce != null)
                        {
                            o = fce.Parent;
                        }
                    }
                }
                else
                {
                    // This could be application.
                    break;
                }
            }

            return nearestVisual;
        }

        internal static bool IsElementEnabled(DependencyObject o)
        {
            bool enabled = true;
            UIElement uie = o as UIElement;
            ContentElement ce = (uie == null) ? o as ContentElement : null;
            UIElement3D uie3D = (uie == null && ce == null) ? o as UIElement3D : null;

            if (uie != null)
            {
                enabled = uie.IsEnabled;
            }
            else if (ce != null)
            {
                enabled = ce.IsEnabled;
            }
            else if (uie3D != null)
            {
                enabled = uie3D.IsEnabled;
            }

            return enabled;
        }

        internal static PopupControlService Current
        {
            get
            {
                return FrameworkElement.PopupControlService;
            }
        }

        internal ToolTip CurrentToolTip
        {
            get
            {
                return _currentToolTip;
            }
        }

        private DispatcherTimer ToolTipTimer
        {
            get
            {
                return _toolTipTimer;
            }
            set
            {
                ResetToolTipTimer();
                _toolTipTimer = value;
            }
        }

        /// <summary>
        ///     Returns the UIElement target
        /// </summary>
        private static UIElement GetTarget(DependencyObject o)
        {
            UIElement uie = o as UIElement;
            if (uie == null)
            {
                ContentElement ce = o as ContentElement;
                if (ce != null)
                {
                    DependencyObject ceParent = FindContentElementParent(ce);

                    // attempt to cast to a UIElement
                    uie = ceParent as UIElement;
                    if (uie == null)
                    {
                        // target can't be a UIElement3D - so get the nearest containing UIElement
                        UIElement3D uie3D = ceParent as UIElement3D;
                        if (uie3D != null)
                        {
                            uie = UIElementHelper.GetContainingUIElement2D(uie3D);
                        }
                    }
                }
                else
                {
                    // it wasn't a UIElement or ContentElement, try one last cast to UIElement3D
                    // target can't be a UIElement3D - so get the nearest containing UIElement
                    UIElement3D uie3D = o as UIElement3D;

                    if (uie3D != null)
                    {
                        uie = UIElementHelper.GetContainingUIElement2D(uie3D);
                    }
                }
            }

            return uie;
        }

        /// <summary>
        ///     Indicates whether the service owns the tooltip
        /// </summary>
        internal static readonly DependencyProperty ServiceOwnedProperty =
            DependencyProperty.RegisterAttached("ServiceOwned",                 // Name
                                                typeof(bool),                   // Type
                                                typeof(PopupControlService),    // Owner
                                                new FrameworkPropertyMetadata(BooleanBoxes.FalseBox));

        /// <summary>
        ///     Stores the original element on which to fire the closed event
        /// </summary>
        internal static readonly DependencyProperty OwnerProperty =
            DependencyProperty.RegisterAttached("Owner",                        // Name
                                                typeof(DependencyObject),       // Type
                                                typeof(PopupControlService),    // Owner
                                                new FrameworkPropertyMetadata((DependencyObject)null, // Default Value
                                                                               new PropertyChangedCallback(OnOwnerChanged)));

        // When the owner changes, coerce all attached properties from the service
        private static void OnOwnerChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
        {
            if (o is ContextMenu)
            {
                o.CoerceValue(ContextMenu.HorizontalOffsetProperty);
                o.CoerceValue(ContextMenu.VerticalOffsetProperty);
                o.CoerceValue(ContextMenu.PlacementTargetProperty);
                o.CoerceValue(ContextMenu.PlacementRectangleProperty);
                o.CoerceValue(ContextMenu.PlacementProperty);
                o.CoerceValue(ContextMenu.HasDropShadowProperty);
            }
            else if (o is ToolTip)
            {
                o.CoerceValue(ToolTip.HorizontalOffsetProperty);
                o.CoerceValue(ToolTip.VerticalOffsetProperty);
                o.CoerceValue(ToolTip.PlacementTargetProperty);
                o.CoerceValue(ToolTip.PlacementRectangleProperty);
                o.CoerceValue(ToolTip.PlacementProperty);
                o.CoerceValue(ToolTip.HasDropShadowProperty);
            }
        }

        // Returns the value of dp on the Owner if it is set there,
        // otherwise returns the value set on o (the tooltip or contextmenu)
        internal static object CoerceProperty(DependencyObject o, object value, DependencyProperty dp)
        {
            DependencyObject owner = (DependencyObject)o.GetValue(OwnerProperty);
            if (owner != null)
            {
                bool hasModifiers;
                if (owner.GetValueSource(dp, null, out hasModifiers) != BaseValueSourceInternal.Default || hasModifiers)
                {
                    // Return a value if it is set on the owner
                    return owner.GetValue(dp);
                }
                else if (dp == ToolTip.PlacementTargetProperty || dp == ContextMenu.PlacementTargetProperty)
                {
                    UIElement uie = GetTarget(owner);

                    // If it is the PlacementTarget property, return the owner itself
                    if (uie != null)
                        return uie;
                }
            }
            return value;
        }

        #endregion

        #region Data

        private DispatcherTimer _toolTipTimer;
        private bool _quickShow = false;
        private WeakReference _lastMouseDirectlyOver;
        private WeakReference _lastMouseOverWithToolTip;
        private WeakReference _lastChecked;
        private ToolTip _currentToolTip;
        private DispatcherTimer _forceCloseTimer;
        private bool _ownToolTip;

        #endregion
    }
}


