// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace System.ComponentModel
{
    /// <summary>
    /// Specifies the category in which the property or event will be displayed in a
    /// visual designer.
    /// </summary>
    [AttributeUsage(AttributeTargets.All)]
    public class CategoryAttribute : Attribute
    {
        private volatile bool _localized;

        private readonly object _locker = new object();

        /// <summary>
        /// Provides the actual category name.
        /// </summary>
        private string _categoryValue;

        /// <summary>
        /// Gets the action category attribute.
        /// </summary>
        public static CategoryAttribute Action => field ??= new CategoryAttribute(nameof(Action));

        /// <summary>
        /// Gets the appearance category attribute.
        /// </summary>
        public static CategoryAttribute Appearance => field ??= new CategoryAttribute(nameof(Appearance));

        /// <summary>
        /// Gets the asynchronous category attribute.
        /// </summary>
        public static CategoryAttribute Asynchronous => field ??= new CategoryAttribute(nameof(Asynchronous));

        /// <summary>
        /// Gets the behavior category attribute.
        /// </summary>
        public static CategoryAttribute Behavior => field ??= new CategoryAttribute(nameof(Behavior));

        /// <summary>
        /// Gets the data category attribute.
        /// </summary>
        public static CategoryAttribute Data => field ??= new CategoryAttribute(nameof(Data));

        /// <summary>
        /// Gets the default category attribute.
        /// </summary>
        public static CategoryAttribute Default => field ??= new CategoryAttribute();

        /// <summary>
        /// Gets the design category attribute.
        /// </summary>
        public static CategoryAttribute Design => field ??= new CategoryAttribute(nameof(Design));

        /// <summary>
        /// Gets the drag and drop category attribute.
        /// </summary>
        public static CategoryAttribute DragDrop => field ??= new CategoryAttribute(nameof(DragDrop));

        /// <summary>
        /// Gets the focus category attribute.
        /// </summary>
        public static CategoryAttribute Focus => field ??= new CategoryAttribute(nameof(Focus));

        /// <summary>
        /// Gets the format category attribute.
        /// </summary>
        public static CategoryAttribute Format => field ??= new CategoryAttribute(nameof(Format));

        /// <summary>
        /// Gets the keyboard category attribute.
        /// </summary>
        public static CategoryAttribute Key => field ??= new CategoryAttribute(nameof(Key));

        /// <summary>
        /// Gets the layout category attribute.
        /// </summary>
        public static CategoryAttribute Layout => field ??= new CategoryAttribute(nameof(Layout));

        /// <summary>
        /// Gets the mouse category attribute.
        /// </summary>
        public static CategoryAttribute Mouse => field ??= new CategoryAttribute(nameof(Mouse));

        /// <summary>
        /// Gets the window style category attribute.
        /// </summary>
        public static CategoryAttribute WindowStyle => field ??= new CategoryAttribute(nameof(WindowStyle));

        /// <summary>
        /// Initializes a new instance of the <see cref='System.ComponentModel.CategoryAttribute'/>
        /// class with the default category.
        /// </summary>
        public CategoryAttribute() : this(nameof(Default))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref='System.ComponentModel.CategoryAttribute'/>
        /// class with the specified category name.
        /// </summary>
        public CategoryAttribute(string category)
        {
            _categoryValue = category;
        }

        /// <summary>
        /// Gets the name of the category for the property or event that this attribute is
        /// bound to.
        /// </summary>
        public string Category
        {
            get
            {
                if (!_localized)
                {
                    lock (_locker)
                    {
                        string? localizedValue = GetLocalizedString(_categoryValue);
                        if (localizedValue != null)
                        {
                            _categoryValue = localizedValue;
                        }

                        _localized = true;
                    }
                }

                return _categoryValue;
            }
        }

        public override bool Equals([NotNullWhen(true)] object? obj) =>
            obj is CategoryAttribute other && other.Category == Category;

        public override int GetHashCode() => Category?.GetHashCode() ?? 0;

        /// <summary>
        /// Looks up the localized name of a given category.
        /// </summary>
        protected virtual string? GetLocalizedString(string value) => value switch
        {
            "Action" => SR.PropertyCategoryAction,
            "Appearance" => SR.PropertyCategoryAppearance,
            "Asynchronous" => SR.PropertyCategoryAsynchronous,
            "Behavior" => SR.PropertyCategoryBehavior,
            "Config" => SR.PropertyCategoryConfig,
            "Data" => SR.PropertyCategoryData,
            "DDE" => SR.PropertyCategoryDDE,
            "Default" => SR.PropertyCategoryDefault,
            "Design" => SR.PropertyCategoryDesign,
            "DragDrop" => SR.PropertyCategoryDragDrop,
            "Focus" => SR.PropertyCategoryFocus,
            "Font" => SR.PropertyCategoryFont,
            "Format" => SR.PropertyCategoryFormat,
            "Key" => SR.PropertyCategoryKey,
            "Layout" => SR.PropertyCategoryLayout,
            "List" => SR.PropertyCategoryList,
            "Mouse" => SR.PropertyCategoryMouse,
            "Position" => SR.PropertyCategoryPosition,
            "Scale" => SR.PropertyCategoryScale,
            "Text" => SR.PropertyCategoryText,
            "WindowStyle" => SR.PropertyCategoryWindowStyle,
            _ => null
        };

        public override bool IsDefaultAttribute() => Category == Default.Category;
    }
}
