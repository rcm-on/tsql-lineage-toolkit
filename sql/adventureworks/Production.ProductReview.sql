CREATE TABLE [Production].[ProductReview] (

  [ProductReviewID] int NOT NULL,
  [ProductID] int NOT NULL,
  [ReviewerName] Name NOT NULL,
  [ReviewDate] datetime NOT NULL,
  [EmailAddress] nvarchar(50) NOT NULL,
  [Rating] int NOT NULL,
  [Comments] nvarchar(3850) NULL,
  [ModifiedDate] datetime NOT NULL
);
