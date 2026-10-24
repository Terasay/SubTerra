# Публикация Subterra на GitHub

Корень репозитория — папка, в которой лежат `README.md`, `.gitignore`, `global.json` и каталог `SubTerra`. Само приложение находится во вложенной папке `SubTerra`, тесты и диагностика — рядом с ней.

## Текущий репозиторий

Подключён [Terasay/SubTerra](https://github.com/Terasay/SubTerra), рабочая ветка — `master`. История сохранена при переносе корня Git из папки приложения в корень решения. Теперь репозиторий включает все проекты и документацию.

В GitHub Desktop добавьте **корень решения** через **File → Add local repository**. Если раньше была добавлена только папка приложения, выберите новый корень. После просмотра локального коммита отправьте изменения кнопкой **Push origin**. Эквивалент из командной строки:

```powershell
git status
git log -1 --oneline
git push origin master
```

Следующие разделы нужны при публикации отдельной копии в новый репозиторий.

## Через GitHub Desktop

1. Откройте **File → Add local repository** и выберите корень проекта.
2. Проверьте вкладку **Changes**: сборки `bin`, `obj`, настройки `.vs`, файлы `*.user`, записи `*.subterra` и учётные данные не должны попадать в коммит.
3. Если есть неперенесённые изменения, создайте коммит с понятным описанием.
4. Нажмите **Publish repository**.
5. Имя репозитория: `Subterra`. Описание можно взять ниже. Выберите владельца и нужную видимость; флажок **Keep this code private** означает закрытый репозиторий.
6. Нажмите **Publish Repository**. После публикации проверьте вкладку **Actions** на GitHub.

Эти шаги соответствуют инструкциям GitHub: [добавление локального репозитория](https://docs.github.com/en/desktop/adding-and-cloning-repositories/adding-a-repository-from-your-local-computer-to-github-desktop) и [публикация проекта](https://docs.github.com/en/desktop/adding-and-cloning-repositories/adding-an-existing-project-to-github-using-github-desktop).

## Через командную строку

Для отдельной локальной копии без настроенного `origin` создайте на GitHub пустой репозиторий. При создании не добавляйте README, `.gitignore` или лицензию: локальные файлы уже подготовлены. Затем из корня этой копии выполните команды, заменив `OWNER` на своего пользователя или организацию:

```powershell
git remote add origin https://github.com/OWNER/Subterra.git
git push -u origin HEAD
```

Перед первой отправкой можно посмотреть состояние и первый коммит:

```powershell
git status
git log -1 --oneline
git remote -v
```

## Описание репозитория

```text
Subterra — Windows-прототип LiDAR-картографирования подземных пространств: симуляция сканирования, 3D-пещеры и осмотр от первого лица. C#, .NET 9, WPF, HelixToolkit.
```

Темы для поля **Topics**:

```text
csharp dotnet wpf lidar point-cloud 3d-visualization helix-toolkit simulation cave-mapping
```

## Автоматическая проверка

Workflow [ci.yml](../.github/workflows/ci.yml) запускается после отправки в `master` или `main`, для pull request в эти ветки и вручную через **Actions → Run workflow**. Он восстанавливает зафиксированные зависимости, собирает решение и запускает тесты на Windows с .NET 9. Токену workflow разрешено только чтение содержимого репозитория.

Используются официальные [actions/checkout](https://github.com/actions/checkout) и [actions/setup-dotnet](https://github.com/actions/setup-dotnet), закреплённые по SHA коммита. Проверка не публикует приложение и не создаёт релизы. Тесты данных и модели представления не заменяют ручную проверку окна и DirectX на целевом компьютере.

## Состав и лицензия

В репозитории хранятся исходники, тесты, документация, концепция/ТЗ `Subterra.docx` и результаты воспроизводимого CPU-замера. Локальная учётная запись администратора находится в профиле Windows и не переносится через Git.

Лицензия проекта пока не выбрана. Файл `LICENSE` следует добавить отдельно после выбора условий распространения. Зависимости сохраняют собственные лицензии.
