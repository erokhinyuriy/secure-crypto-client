using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using System;
using System.Collections.Specialized;
using SecureCryptoClient.ViewModels;

namespace SecureCryptoClient.Views;

public partial class MainChatView : UserControl
{
    public MainChatView()
    {
        InitializeComponent();

        DataContextChanged += MainChatView_DataContextChanged;
        ChatScrollViewer.ScrollChanged += ChatScrollViewer_ScrollChanged;

        // ЖЕЛЕЗОБЕТОННЫЙ ФИКС КЛИКА: Напрямую подписываем круглую кнопку на метод скролла вниз!
        ScrollDownBtn.Click += ScrollDownBtn_Click;
    }

    private void ScrollDownBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MainChatViewModel vm)
        {
            // Скрываем кнопку на экране
            vm.IsScrollButtonVisible = false;
        }

        // Мгновенно роняем ленту чата в самый низ к свежим сообщениям
        ScrollToLastMessage();
    }

    private void ChatScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is MainChatViewModel vm)
        {
            double distanceFromBottom = ChatScrollViewer.Extent.Height - ChatScrollViewer.Viewport.Height - ChatScrollViewer.Offset.Y;

            // Показываем кнопку, если прокрутили вверх более чем на 150 пикселей
            vm.IsScrollButtonVisible = distanceFromBottom > 150;
        }
    }

    private void MainChatView_DataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainChatViewModel vm)
        {
            ScrollToLastMessage();
            vm.Messages.CollectionChanged += Messages_CollectionChanged;
        }
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Replace)
        {
            ScrollToLastMessage();
        }
    }

    private void ScrollToLastMessage()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var itemsControl = ChatScrollViewer.Content as ItemsControl;
                if (itemsControl == null)
                {
                    // Подстраховка: если ItemsControl обернут в StackPanel (как в вашей текущей разметке)
                    var stackPanel = ChatScrollViewer.Content as StackPanel;
                    itemsControl = stackPanel?.Children[0] as ItemsControl;
                }

                if (itemsControl == null || itemsControl.Presenter == null) return;

                var visualChildren = itemsControl.Presenter.Panel?.Children;
                if (visualChildren == null || visualChildren.Count == 0) return;

                var lastVisualMessage = visualChildren[visualChildren.Count - 1];

                // Перебрасываем фокус на последнее сообщение
                lastVisualMessage.BringIntoView();
            }
            catch { }
        }, DispatcherPriority.Render);
    }
}
