var express = require('express'),
    async = require('async'),
    { Pool } = require('pg'),
    cookieParser = require('cookie-parser'),
    path = require('path'),
    app = express(),
    server = require('http').Server(app),
    io = require('socket.io')(server);

var port = process.env.PORT || 4000;

// Cache config so we don't call Secrets Manager on every retry
var cachedConfig = null;

// Get database credentials from Secrets Manager (using IRSA)
async function getConnectionConfig() {
  // Return cached config if available (don't fetch secrets on every retry)
  if (cachedConfig) {
    return cachedConfig;
  }

  const secretArn = process.env.DATABASE_SECRET_ARN;

  if (secretArn) {
    // Fetch credentials from Secrets Manager using IRSA
    const { SecretsManagerClient, GetSecretValueCommand } = require('@aws-sdk/client-secrets-manager');

    const region = process.env.AWS_REGION || 'us-west-2';
    const host = process.env.DATABASE_HOST;
    const dbPort = parseInt(process.env.DATABASE_PORT || '5432');
    const database = process.env.DATABASE_NAME || 'votes';

    console.log(`Fetching database credentials from Secrets Manager using IRSA...`);

    const client = new SecretsManagerClient({ region });
    const response = await client.send(new GetSecretValueCommand({ SecretId: secretArn }));
    const secret = JSON.parse(response.SecretString);

    console.log(`Using Secrets Manager credentials for RDS: ${host}:${dbPort} as ${secret.username}`);

    cachedConfig = {
      host: host,
      port: dbPort,
      user: secret.username,
      password: secret.password,
      database: database,
      ssl: {
        rejectUnauthorized: false
      },
      // Limit pool size to avoid exhausting RDS connections
      max: 3,
      idleTimeoutMillis: 30000,
      connectionTimeoutMillis: 5000
    };
    return cachedConfig;
  } else {
    // Use traditional connection string
    const connectionString = process.env.DATABASE_URL || 'postgres://postgres:postgres@db/postgres';
    console.log('Using password authentication for database');
    cachedConfig = { connectionString, max: 3 };
    return cachedConfig;
  }
}

io.on('connection', function (socket) {
  socket.emit('message', { text : 'Welcome!' });

  socket.on('subscribe', function (data) {
    socket.join(data.channel);
  });
});

// Initialize database connection with retry
async function initializeDatabase() {
  // Create pool once, outside retry loop
  const config = await getConnectionConfig();
  const pool = new Pool(config);

  // Handle pool errors
  pool.on('error', (err) => {
    console.error('Unexpected pool error:', err.message);
  });

  // Simple retry loop with async/await
  const maxRetries = 100;
  const retryInterval = 3000; // 3 seconds between retries

  for (let attempt = 1; attempt <= maxRetries; attempt++) {
    try {
      const client = await pool.connect();
      console.log("Connected to db");
      getVotes(client);
      return; // Success, exit function
    } catch (err) {
      console.error(`Waiting for db (attempt ${attempt}/${maxRetries}):`, err.message);
      if (attempt < maxRetries) {
        await new Promise(resolve => setTimeout(resolve, retryInterval));
      }
    }
  }

  console.error("Giving up connecting to database after " + maxRetries + " attempts");
}

function getVotes(client) {
  client.query('SELECT vote, COUNT(id) AS count FROM votes GROUP BY vote', [], function(err, result) {
    if (err) {
      console.error("Error performing query: " + err);
    } else {
      var votes = collectVotesFromResult(result);
      io.sockets.emit("scores", JSON.stringify(votes));
    }

    setTimeout(function() {getVotes(client) }, 1000);
  });
}

function collectVotesFromResult(result) {
  var votes = {a: 0, b: 0};

  result.rows.forEach(function (row) {
    votes[row.vote] = parseInt(row.count);
  });

  return votes;
}

app.use(cookieParser());
app.use(express.urlencoded());
app.use(express.static(__dirname + '/views'));

app.get('/', function (req, res) {
  res.sendFile(path.resolve(__dirname + '/views/index.html'));
});

// Start the server and initialize database
server.listen(port, function () {
  var port = server.address().port;
  console.log('App running on port ' + port);
  initializeDatabase();
});
