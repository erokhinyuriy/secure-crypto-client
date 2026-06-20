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
    }

    private void MainChatView_DataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainChatViewModel vm)
        {
            // При первой загрузке чата принудительно крутим в самый низ
            ScrollToLastMessage();

            // Подписываемся на появление новых сообщений
            vm.Messages.CollectionChanged += Messages_CollectionChanged;
        }
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Если в коллекцию добавилось новое сообщение (отправлено или получено)
        if (e.Action == NotifyCollectionChangedAction.Add || e.Action == NotifyCollectionChangedAction.Replace)
        {
            ScrollToLastMessage();
        }
    }

    // МЕТОД ОДНОКНОПОЧНОГО НАДЁЖНОГО АВТОСКРОЛЛА
    private void ScrollToLastMessage()
    {
        // Даем микропаузу в приоритете Render, чтобы Avalonia успела создать "бабл" на экране
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // 1. Находим наш ItemsControl, внутри которого лежат сообщения
                // Мы можем найти его по типу в структуре ScrollViewer
                var itemsControl = ChatScrollViewer.Content as ItemsControl;
                if (itemsControl == null || itemsControl.Presenter == null) return;

                // 2. Находим самый последний отрендеренный элемент (наше новое сообщение)
                var visualChildren = itemsControl.Presenter.Panel?.Children;
                if (visualChildren == null || visualChildren.Count == 0) return;

                var lastVisualMessage = visualChildren[visualChildren.Count - 1];

                // 3. ЖЕЛЕЗОБЕТОННЫЙ ХУК: Принудительно заставляем операционную систему 
                // развернуть ScrollViewer так, чтобы это последнее сообщение полностью встало в фокус!
                // Этот метод плевать хотел на отрицательные Padding и Grid. Скроллит всегда.
                lastVisualMessage.BringIntoView();
            }
            catch
            {
                // Подстраховка на случай, если коллекция пуста во время инициализации
            }
        }, DispatcherPriority.Render);
    }
}
