﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web.Mvc;
using System.Web.Security;
using MVCForum.Domain.Constants;
using MVCForum.Domain.DomainModel;
using MVCForum.Domain.Interfaces.Services;
using MVCForum.Domain.Interfaces.UnitOfWork;
using MVCForum.Utilities;
using MVCForum.Website.ViewModels;

namespace MVCForum.Website.Controllers
{
    public class TopicController : BaseController
    {
        private readonly ITopicService _topicService;
        private readonly IPostService _postService;
        private readonly ITopicTagService _topicTagService;
        private readonly ICategoryService _categoryService;
        private readonly ICategoryNotificationService _categoryNotificationService;
        private readonly ITopicNotificationService _topicNotificationService;
        private readonly IMembershipUserPointsService _membershipUserPointsService;
        private readonly IEmailService _emailService;

        public TopicController(ILoggingService loggingService, IUnitOfWorkManager unitOfWorkManager, IMembershipService membershipService, IRoleService roleService, ITopicService topicService, IPostService postService,
            ICategoryService categoryService, ILocalizationService localizationService, ISettingsService settingsService, ITopicTagService topicTagService, IMembershipUserPointsService membershipUserPointsService,
            ICategoryNotificationService categoryNotificationService, IEmailService emailService, ITopicNotificationService topicNotificationService)
            : base(loggingService, unitOfWorkManager, membershipService, localizationService, roleService, settingsService)
        {
            _topicService = topicService;
            _postService = postService;
            _categoryService = categoryService;
            _topicTagService = topicTagService;
            _membershipUserPointsService = membershipUserPointsService;
            _categoryNotificationService = categoryNotificationService;
            _emailService = emailService;
            _topicNotificationService = topicNotificationService;
        }

        [Authorize]
        public ActionResult Create()
        {
            using (UnitOfWorkManager.NewUnitOfWork())
            {
                    var allowedCategories = _categoryService.GetAllowedCategories(UsersRole).ToList();
                    if (allowedCategories.Any())
                    {
                        var viewModel = new CreateTopicViewModel
                        {
                            Categories = allowedCategories
                        };

                        return View(viewModel);
                    }
                    return ErrorToHomePage(LocalizationService.GetResourceString("Errors.NoPermission"));
            }            
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public ActionResult Create(CreateTopicViewModel topicViewModel)
        {
            if (ModelState.IsValid)
            {

                // Quick check to see if user is locked out, when logged in
                if (LoggedOnUser.IsLockedOut | !LoggedOnUser.IsApproved)
                {
                    FormsAuthentication.SignOut();
                    return ErrorToHomePage(LocalizationService.GetResourceString("Errors.NoAccess"));
                }

                var successfullyCreated = false;
                Category category;
                var topic = new Topic();

                using (var unitOfWork = UnitOfWorkManager.NewUnitOfWork())
                {
                    // Not using automapper for this one only, as a topic is a post and topic in one
                    category = _categoryService.Get(topicViewModel.Category);

                    // First check this user is allowed to create topics in this category
                    var permissions = RoleService.GetPermissions(category, UsersRole);

                    // Check this users role has permission to create a post
                    if (permissions[AppConstants.PermissionDenyAccess].IsTicked && permissions[AppConstants.PermissionReadOnly].IsTicked)
                    {
                        // Throw exception so Ajax caller picks it up
                        ModelState.AddModelError(string.Empty, LocalizationService.GetResourceString("Errors.NoPermission"));
                    }
                    else
                    {
                        topic = new Topic
                        {
                            Name = StringUtils.GetSafeHtml(topicViewModel.Name),
                            Category = category,
                            User = LoggedOnUser
                        };

                        topicViewModel.Content = StringUtils.GetSafeHtml(topicViewModel.Content, true);

                        if (!string.IsNullOrEmpty(topicViewModel.Content))
                        {
                            // Update the users points score for posting
                            _membershipUserPointsService.Add(new MembershipUserPoints
                            {
                                Points = SettingsService.GetSettings().PointsAddedPerPost,
                                User = LoggedOnUser
                            });

                            // Create the topic (The topic service creates the related post)
                            topic = _topicService.Add(topic);

                            unitOfWork.SaveChanges();

                            _topicService.AddLastPost(topic, topicViewModel.Content);

                            // Add the tags if any too
                            if (!string.IsNullOrEmpty(topicViewModel.Tags))
                            {
                                _topicTagService.Add(StringUtils.SafePlainText(topicViewModel.Tags.ToLower()), topic);
                            }

                            // Subscribe the user to the topic as they have checked the checkbox
                            if(topicViewModel.SubscribeToTopic)
                            {
                                // Create the notification
                                var topicNotification = new TopicNotification
                                {
                                    Topic = topic,
                                    User = LoggedOnUser
                                };
                                //save
                                _topicNotificationService.Add(topicNotification);
                            }

                            try
                            {
                                unitOfWork.Commit();
                                successfullyCreated = true;
                            }
                            catch (Exception ex)
                            {
                                unitOfWork.Rollback();
                                LoggingService.Error(ex);
                                ModelState.AddModelError(string.Empty, LocalizationService.GetResourceString("Errors.GenericMessage"));
                            }
                        }
                        else
                        {
                            ModelState.AddModelError(string.Empty, LocalizationService.GetResourceString("Errors.NoAccess"));
                        }
                    }
                }

                using (UnitOfWorkManager.NewUnitOfWork())
                {
                    if (successfullyCreated)
                    {
                        // Success so now send the emails
                        NotifyNewTopics(category);
                        // Redirect to the newly created topic
                        return Redirect(string.Format("{0}?postbadges=true", topic.NiceUrl));
                    }

                    var allowedCategories = _categoryService.GetAllowedCategories(UsersRole).ToList();
                    if (allowedCategories.Any())
                    {
                        topicViewModel.Categories = allowedCategories;
                    }
                }
                return View(topicViewModel);
            }

            return ErrorToHomePage(LocalizationService.GetResourceString("Errors.NoPermission"));
        }

        public ActionResult Show(string slug, int? p)
        {
            // Set the page index
            var pageIndex = p ?? 1;

            using (var unitOfWork = UnitOfWorkManager.NewUnitOfWork())
            {
                // Get the topic
                var topic = _topicService.GetTopicBySlug(slug);

                if (topic != null)
                {
                    // Note: Don't use topic.Posts as its not a very efficient SQL statement
                    // Use the post service to get them as it includes other used entities in one
                    // statement rather than loads of sql selects
                    // --- Get all the posts in this topic and put into a full paged list
                    //var posts = new PagedFullList<Post>(topic.Posts.OrderBy(x => x.DateCreated),
                    //                        pageIndex,
                    //                        SettingsService.GetSettings().PostsPerPage,
                    //                        topic.Posts.Count());

                    var posts = _postService.GetPagedPostsByTopic(pageIndex,
                                                                  SettingsService.GetSettings().PostsPerPage,
                                                                  int.MaxValue, 
                                                                  topic.Id);

                    // Get the permissions for the category that this topic is in
                    var permissions = RoleService.GetPermissions(topic.Category, UsersRole);

                    // If this user doesn't have access to this topic then
                    // redirect with message
                    if (permissions[AppConstants.PermissionDenyAccess].IsTicked)
                    {
                        return ErrorToHomePage(LocalizationService.GetResourceString("Errors.NoPermission"));
                    }

                    var isSubscribed = LoggedOnUser != null && (_topicNotificationService.GetByUserAndTopic(LoggedOnUser, topic).Any());

                    // Populate the view model for this page
                    var viewModel = new ShowTopicViewModel
                    {
                        Topic = topic,
                        Posts = posts,
                        PageIndex = posts.PageIndex,
                        TotalCount = posts.TotalCount,
                        Permissions = permissions,
                        User = LoggedOnUser,
                        IsSubscribed = isSubscribed
                    };

                    // User has permission lets update the topic view count
                    // but only if this topic doesn't belong to the user looking at it
                    var addView = !(LoggedOnUser != null && LoggedOnUser.Id == topic.User.Id);

                    if (!BotUtils.UserIsBot() && addView)
                    {
                        // Cool, user doesn't own this topic
                        topic.Views = (topic.Views + 1);
                        try
                        {
                            unitOfWork.Commit();
                        }
                        catch (Exception ex)
                        {
                            LoggingService.Error(ex);
                        }
                    }

                    return View(viewModel);
                }

            }
            return ErrorToHomePage(LocalizationService.GetResourceString("Errors.GenericMessage"));
        }


        public ActionResult TopicsByTag(string tag, int? p)
        {
            using (UnitOfWorkManager.NewUnitOfWork())
            {
                // Set the page index
                var pageIndex = p ?? 1;

                // Get the topics
                var topics = _topicService.GetPagedTopicsByTag(pageIndex,
                                                           SettingsService.GetSettings().TopicsPerPage,
                                                           AppConstants.ActiveTopicsListSize,
                                                           tag);

                // Get all the categories for this topic collection
                var categories = topics.Select(x => x.Category).Distinct();

                // create the view model
                var viewModel = new TagTopicsViewModel
                {
                    Topics = topics,
                    AllPermissionSets = new Dictionary<Category, PermissionSet>(),
                    PageIndex = pageIndex,
                    TotalCount = topics.TotalCount,
                    User = LoggedOnUser,
                    Tag = tag
                };

                // loop through the categories and get the permissions
                foreach (var category in categories)
                {
                    var permissionSet = RoleService.GetPermissions(category, UsersRole);
                    viewModel.AllPermissionSets.Add(category, permissionSet);
                }
                return View(viewModel);
            }
        }
        

        private void NotifyNewTopics(Category cat)
        {
                // TODO: This really needs to be an async call so it doesn't hang when a user creates  
                // TODO: a topic if there are 1000's of users

                // Get all notifications for this category
                var notifications = _categoryNotificationService.GetByCategory(cat).Select(x => x.User.Id).ToList();

                if(notifications.Any())
                {
                    // remove the current user from the notification, don't want to notify yourself that you 
                    // have just made a topic!
                    notifications.Remove(LoggedOnUser.Id);

                    if(notifications.Count > 0)
                    {
                        // Now get all the users that need notifying
                        var usersToNotify = MembershipService.GetUsersById(notifications);

                        // Create the email
                        var sb = new StringBuilder();
                        sb.AppendFormat("<p>{0}</p>", string.Format(LocalizationService.GetResourceString("Topic.Notification.NewTopics"), cat.Name));
                        sb.AppendFormat("<p>{0}</p>", string.Concat(SettingsService.GetSettings().ForumUrl, cat.NiceUrl));

                        // create the emails
                        var emails = usersToNotify.Select(user => new Email
                        {
                            Body = _emailService.EmailTemplate(user.UserName, sb.ToString()),
                            EmailFrom = SettingsService.GetSettings().NotificationReplyEmail,
                            EmailTo = user.Email,
                            NameTo = user.UserName,
                            Subject = string.Concat(LocalizationService.GetResourceString("Topic.Notification.Subject"), SettingsService.GetSettings().ForumName)
                        }).ToList();

                        // and now pass the emails in to be sent
                        _emailService.SendMail(emails); 
                    }
                }
        }

    }
}
